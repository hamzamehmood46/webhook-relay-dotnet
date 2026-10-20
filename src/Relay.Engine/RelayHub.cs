using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Relay.Engine;

/// <summary>
/// Receives webhooks from providers, proves they are genuine, drops repeats, routes each event to the right
/// destinations and delivers it with retries, dead letters, replay and a circuit breaker for destinations that are down.
/// One hub is one customer's workspace; nothing is shared between hubs.
/// </summary>
public sealed partial class RelayHub
{
    public const int MaxSources = 20;
    public const int MaxDestinations = 20;
    public const int MaxRoutes = 60;
    public const int MaxEventsKept = 1000;

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly RelayOptions _options;
    private readonly Dictionary<Guid, Source> _sources = [];
    private readonly Dictionary<Guid, Destination> _destinations = [];
    private readonly List<Route> _routes = [];
    private readonly Dictionary<Guid, InboundEvent> _events = [];
    private readonly List<Guid> _eventOrder = [];
    private readonly List<Delivery> _deliveries = [];
    private readonly Dictionary<(Guid Source, string ProviderId), (DateTimeOffset At, Guid EventId)> _seen = [];
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _recent = [];

    public RelayHub(TimeProvider clock, RelayOptions? options = null)
    {
        _clock = clock;
        _options = options ?? new RelayOptions();
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    // ---- Sources ----

    public Created<Source> AddSource(string name, SignatureScheme scheme, string? secret = null, string eventIdPath = "id", string eventTypePath = "type", int ratePerMinute = 600)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 60)
        {
            return new(false, "A source needs a name of up to 60 characters.", null);
        }

        if (string.IsNullOrWhiteSpace(eventIdPath) || string.IsNullOrWhiteSpace(eventTypePath))
        {
            return new(false, "Say where to find the event id and the event type.", null);
        }

        if (ratePerMinute is < 1 or > 100_000)
        {
            return new(false, "The rate limit must be between 1 and 100000 events a minute.", null);
        }

        lock (_gate)
        {
            if (_sources.Count >= MaxSources)
            {
                return new(false, $"A workspace can have up to {MaxSources} sources.", null);
            }

            var source = new Source
            {
                Id = Guid.NewGuid(),
                Name = name.Trim(),
                Scheme = scheme,
                Secret = scheme == SignatureScheme.None ? "" : string.IsNullOrWhiteSpace(secret) ? Signing.NewSecret("whsec_") : secret.Trim(),
                EventIdPath = eventIdPath.Trim(),
                EventTypePath = eventTypePath.Trim(),
                RatePerMinute = ratePerMinute,
            };
            _sources[source.Id] = source;
            return new(true, "Source created.", source.Snapshot());
        }
    }

    /// <summary>Switches to a new secret. The old one keeps working for the grace period so no event is rejected while the provider catches up.</summary>
    public Created<Source> RotateSourceSecret(Guid id, string? newSecret, TimeSpan grace)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(id, out var source))
            {
                return new(false, "No such source.", null);
            }

            if (source.Scheme == SignatureScheme.None)
            {
                return new(false, "This source does not use a secret.", null);
            }

            source.PreviousSecret = source.Secret;
            source.PreviousSecretValidUntil = Now + grace;
            source.Secret = string.IsNullOrWhiteSpace(newSecret) ? Signing.NewSecret("whsec_") : newSecret.Trim();
            return new(true, "Secret rotated.", source.Snapshot());
        }
    }

    public Result PauseSource(Guid id, bool paused)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(id, out var source))
            {
                return Result.Fail("No such source.");
            }

            source.Paused = paused;
            return Result.Success(paused ? "Paused. The provider gets a 503 and will retry." : "Resumed.");
        }
    }

    public IReadOnlyList<Source> Sources()
    {
        lock (_gate)
        {
            return _sources.Values.Select(s => s.Snapshot()).OrderBy(s => s.Name).ToList();
        }
    }

    public Source? Source(Guid id)
    {
        lock (_gate)
        {
            return _sources.TryGetValue(id, out var s) ? s.Snapshot() : null;
        }
    }

    public bool OwnsSource(Guid id)
    {
        lock (_gate)
        {
            return _sources.ContainsKey(id);
        }
    }

    // ---- Destinations ----

    public Created<Destination> AddDestination(string name, string url)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 60)
        {
            return new(false, "A destination needs a name of up to 60 characters.", null);
        }

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "demo"))
        {
            return new(false, "The url must be an absolute https://, http:// or demo:// address.", null);
        }

        lock (_gate)
        {
            if (_destinations.Count >= MaxDestinations)
            {
                return new(false, $"A workspace can have up to {MaxDestinations} destinations.", null);
            }

            var destination = new Destination { Id = Guid.NewGuid(), Name = name.Trim(), Url = url!.Trim(), Secret = Signing.NewSecret("whsec_") };
            _destinations[destination.Id] = destination;
            return new(true, "Destination created.", destination.Snapshot());
        }
    }

    public Result SetDestinationEnabled(Guid id, bool enabled)
    {
        lock (_gate)
        {
            if (!_destinations.TryGetValue(id, out var destination))
            {
                return Result.Fail("No such destination.");
            }

            destination.Enabled = enabled;
            destination.DisabledReason = enabled ? null : "Switched off by hand.";
            if (enabled)
            {
                destination.ConsecutiveFailures = 0;
                destination.CircuitOpenUntil = null;
            }

            return Result.Success(enabled ? "Enabled. Held deliveries will go out on the next run." : "Disabled. Deliveries are held, not lost.");
        }
    }

    public Created<Destination> RotateDestinationSecret(Guid id)
    {
        lock (_gate)
        {
            if (!_destinations.TryGetValue(id, out var destination))
            {
                return new(false, "No such destination.", null);
            }

            destination.Secret = Signing.NewSecret("whsec_");
            return new(true, "Secret rotated.", destination.Snapshot());
        }
    }

    public IReadOnlyList<Destination> Destinations()
    {
        lock (_gate)
        {
            return _destinations.Values.Select(d => d.Snapshot()).OrderBy(d => d.Name).ToList();
        }
    }

    public Destination? Destination(Guid id)
    {
        lock (_gate)
        {
            return _destinations.TryGetValue(id, out var d) ? d.Snapshot() : null;
        }
    }

    /// <summary>Removes a destination along with its routes. Deliveries still waiting for it are dead-lettered, so they show up instead of vanishing.</summary>
    public Result RemoveDestination(Guid id)
    {
        lock (_gate)
        {
            if (!_destinations.Remove(id))
            {
                return Result.Fail("No such destination.");
            }

            _routes.RemoveAll(r => r.DestinationId == id);
            foreach (var delivery in _deliveries.Where(d => d.DestinationId == id && d.IsOpen && !d.InFlight))
            {
                delivery.Status = DeliveryStatus.DeadLettered;
                delivery.LastError = "The destination was deleted.";
                delivery.CompletedAt = Now;
            }

            return Result.Success("Destination and its routes removed.");
        }
    }

    // ---- Routes ----

    [GeneratedRegex(@"^(\*|[A-Za-z0-9_:\-]+(\.[A-Za-z0-9_:\-]+)*(\.\*)?)$")]
    private static partial Regex EventTypePattern();

    public Created<Route> AddRoute(Guid sourceId, Guid destinationId, string[]? eventTypes = null, PayloadCondition? where = null)
    {
        var patterns = eventTypes is { Length: > 0 } ? eventTypes.Select(t => t.Trim()).ToArray() : ["*"];
        var bad = patterns.FirstOrDefault(p => !EventTypePattern().IsMatch(p));
        if (bad is not null)
        {
            return new(false, $"'{bad}' is not a valid event type. Use an exact type, a prefix such as invoice.*, or *.", null);
        }

        if (where is not null && (string.IsNullOrWhiteSpace(where.Path) || where.Value is null))
        {
            return new(false, "A payload condition needs a path and a value.", null);
        }

        lock (_gate)
        {
            if (!_sources.ContainsKey(sourceId))
            {
                return new(false, "No such source.", null);
            }

            if (!_destinations.ContainsKey(destinationId))
            {
                return new(false, "No such destination.", null);
            }

            if (_routes.Count >= MaxRoutes)
            {
                return new(false, $"A workspace can have up to {MaxRoutes} routes.", null);
            }

            var route = new Route { Id = Guid.NewGuid(), SourceId = sourceId, DestinationId = destinationId, EventTypes = patterns, Where = where };
            _routes.Add(route);
            return new(true, "Route created.", route.Snapshot());
        }
    }

    public Result SetRouteEnabled(Guid id, bool enabled)
    {
        lock (_gate)
        {
            var route = _routes.FirstOrDefault(r => r.Id == id);
            if (route is null)
            {
                return Result.Fail("No such route.");
            }

            route.Enabled = enabled;
            return Result.Success(enabled ? "Route on." : "Route off. New events skip it.");
        }
    }

    public Result RemoveRoute(Guid id)
    {
        lock (_gate)
        {
            return _routes.RemoveAll(r => r.Id == id) > 0 ? Result.Success("Route removed.") : Result.Fail("No such route.");
        }
    }

    public IReadOnlyList<Route> Routes()
    {
        lock (_gate)
        {
            return _routes.Select(r => r.Snapshot()).ToList();
        }
    }

    // ---- Receiving ----

    public IngestResult Ingest(Guid sourceId, IReadOnlyDictionary<string, string> rawHeaders, string body)
    {
        var headers = new Dictionary<string, string>(rawHeaders, StringComparer.OrdinalIgnoreCase);
        body ??= "";

        lock (_gate)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
            {
                return new(IngestOutcome.UnknownSource, "No such source.");
            }

            if (Encoding.UTF8.GetByteCount(body) > _options.MaxBodyBytes)
            {
                return new(IngestOutcome.TooLarge, $"The body is larger than {_options.MaxBodyBytes / 1024} KB.");
            }

            var now = Now;
            var secrets = new List<string> { source.Secret };
            if (source.PreviousSecret is not null && source.PreviousSecretValidUntil > now)
            {
                secrets.Add(source.PreviousSecret);
            }

            switch (Signing.VerifyInbound(source.Scheme, headers, body, secrets, now, _options.Tolerance))
            {
                case VerifyResult.Missing:
                    return new(IngestOutcome.Unauthorized, "The signature header is missing.");
                case VerifyResult.Malformed:
                    return new(IngestOutcome.Unauthorized, "The signature header is malformed.");
                case VerifyResult.Mismatch:
                    return new(IngestOutcome.Unauthorized, "The signature does not match.");
                case VerifyResult.OutsideTolerance:
                    return new(IngestOutcome.Expired, "The signature is valid but the timestamp is too old or too far ahead.");
            }

            if (source.Paused)
            {
                return new(IngestOutcome.Paused, "This source is paused. Try again later.");
            }

            if (!_recent.TryGetValue(source.Id, out var recent))
            {
                _recent[source.Id] = recent = new Queue<DateTimeOffset>();
            }

            while (recent.Count > 0 && now - recent.Peek() >= TimeSpan.FromMinutes(1))
            {
                recent.Dequeue();
            }

            if (recent.Count >= source.RatePerMinute)
            {
                return new(IngestOutcome.RateLimited, $"More than {source.RatePerMinute} events a minute. Slow down.");
            }

            recent.Enqueue(now);

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return new(IngestOutcome.Malformed, "The body is not valid JSON.");
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return new(IngestOutcome.Malformed, "The body must be a JSON object.");
                }

                var providerId = Matching.Extract(source.EventIdPath, headers, document.RootElement) ?? "body:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
                var type = Matching.Extract(source.EventTypePath, headers, document.RootElement) ?? "unknown";

                if (_seen.TryGetValue((source.Id, providerId), out var earlier) && now - earlier.At < _options.DedupeWindow)
                {
                    return new(IngestOutcome.Duplicate, "Already received. Nothing was delivered twice.", earlier.EventId);
                }

                var inbound = new InboundEvent { Id = Guid.NewGuid(), SourceId = source.Id, ProviderEventId = providerId, Type = type, Body = body, ReceivedAt = now };
                _events[inbound.Id] = inbound;
                _eventOrder.Add(inbound.Id);
                _seen[(source.Id, providerId)] = (now, inbound.Id);

                var count = 0;
                var queued = new HashSet<Guid>();
                foreach (var route in _routes.Where(r => r.SourceId == source.Id && r.Enabled))
                {
                    if (!_destinations.TryGetValue(route.DestinationId, out var destination) || !destination.Enabled)
                    {
                        continue;
                    }

                    // Two routes can lead to the same destination; it still gets the event once.
                    if (Matching.TypeMatches(route.EventTypes, type) && Matching.ConditionMatches(route.Where, document.RootElement) && queued.Add(destination.Id))
                    {
                        Queue(inbound, destination.Id, route.Id, null);
                        count++;
                    }
                }

                Trim();
                return new(IngestOutcome.Accepted, count == 0 ? "Stored. No route wanted this event." : $"Accepted for {count} destination(s).", inbound.Id, count);
            }
        }
    }

    private Delivery Queue(InboundEvent inbound, Guid destinationId, Guid? routeId, Guid? replayOf)
    {
        var now = Now;
        var delivery = new Delivery { Id = Guid.NewGuid(), EventId = inbound.Id, DestinationId = destinationId, RouteId = routeId, ReplayOf = replayOf, Status = DeliveryStatus.Pending, CreatedAt = now, NextAttemptAt = now };
        _deliveries.Add(delivery);
        return delivery;
    }

    /// <summary>Keeps memory bounded: when there are too many events, the oldest go, together with their finished deliveries.</summary>
    private void Trim()
    {
        while (_eventOrder.Count > MaxEventsKept)
        {
            var oldest = _eventOrder[0];
            // An event with unfinished work is kept, unless the workspace holds twice the limit: then memory wins.
            if (_eventOrder.Count <= MaxEventsKept * 2 && _deliveries.Any(d => d.EventId == oldest && d.IsOpen))
            {
                break;
            }

            _eventOrder.RemoveAt(0);
            _events.Remove(oldest);
            _deliveries.RemoveAll(d => d.EventId == oldest);
        }

        var cutoff = Now - _options.DedupeWindow;
        foreach (var key in _seen.Where(p => p.Value.At < cutoff).Select(p => p.Key).ToList())
        {
            _seen.Remove(key);
        }
    }

    // ---- Delivering ----

    /// <summary>Attempts every delivery that is due. Returns how many attempts were made.</summary>
    public async Task<int> ProcessDueAsync(ITransport transport, CancellationToken ct = default)
    {
        var claimed = new List<Delivery>();
        lock (_gate)
        {
            var now = Now;
            var probing = new HashSet<Guid>();
            foreach (var delivery in _deliveries.Where(d => d.IsOpen && !d.InFlight && d.NextAttemptAt <= now).OrderBy(d => d.NextAttemptAt).ThenBy(d => d.CreatedAt).ToList())
            {
                if (!_destinations.TryGetValue(delivery.DestinationId, out var destination))
                {
                    delivery.Status = DeliveryStatus.DeadLettered;
                    delivery.LastError = "The destination was deleted.";
                    delivery.CompletedAt = now;
                    continue;
                }

                if (!destination.Enabled)
                {
                    continue;
                }

                if (destination.CircuitOpenUntil is { } until)
                {
                    if (until > now)
                    {
                        continue;
                    }

                    // The breaker has cooled off: let exactly one delivery through as a probe.
                    if (!probing.Add(destination.Id))
                    {
                        continue;
                    }
                }

                delivery.InFlight = true;
                claimed.Add(delivery);
            }
        }

        var attempted = 0;
        try
        {
            foreach (var delivery in claimed)
            {
                OutboundRequest? request;
                lock (_gate)
                {
                    request = Prepare(delivery);
                    if (request is null)
                    {
                        delivery.InFlight = false;
                        continue;
                    }
                }

                TransportResult result;
                try
                {
                    result = await transport.SendAsync(request, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        delivery.InFlight = false;
                    }

                    throw;
                }
                catch (Exception ex)
                {
                    result = new TransportResult(null, ex.Message);
                }

                lock (_gate)
                {
                    Apply(delivery, result);
                    attempted++;
                }
            }
        }
        finally
        {
            // If the run is cancelled part-way, what was claimed but never attempted must be released for the next run.
            lock (_gate)
            {
                foreach (var delivery in claimed)
                {
                    delivery.InFlight = false;
                }
            }
        }

        return attempted;
    }

    /// <summary>Builds the request, or returns null if the destination should not be called right now (it went down or was switched off during this run).</summary>
    private OutboundRequest? Prepare(Delivery delivery)
    {
        var now = Now;
        if (!_destinations.TryGetValue(delivery.DestinationId, out var destination) || !destination.Enabled || !_events.TryGetValue(delivery.EventId, out var inbound))
        {
            return null;
        }

        if (destination.CircuitOpenUntil is { } until && until > now)
        {
            return null;
        }

        var attempt = delivery.Attempts + 1;
        var headers = new Dictionary<string, string>
        {
            [Headers.EventId] = inbound.Id.ToString(),
            [Headers.DeliveryId] = delivery.Id.ToString(),
            [Headers.Attempt] = attempt.ToString(),
            [Headers.EventType] = inbound.Type,
            [Headers.Signature] = Signing.SignOutbound(destination.Secret, inbound.Id, now.ToUnixTimeSeconds(), inbound.Body),
        };
        return new OutboundRequest(destination.Snapshot(), delivery.Id, inbound.Id, inbound.Type, attempt, headers, inbound.Body);
    }

    private void Apply(Delivery delivery, TransportResult result)
    {
        var now = Now;
        delivery.InFlight = false;
        delivery.Attempts++;
        delivery.HistoryList.Add(new Attempt(delivery.Attempts, now, result.StatusCode, result.Error));
        delivery.LastStatusCode = result.StatusCode;
        delivery.LastError = result.Error;

        if (!_destinations.TryGetValue(delivery.DestinationId, out var destination))
        {
            delivery.Status = DeliveryStatus.DeadLettered;
            delivery.LastError = "The destination was deleted.";
            delivery.CompletedAt = now;
            return;
        }

        if (result.IsSuccess)
        {
            delivery.Status = DeliveryStatus.Succeeded;
            delivery.CompletedAt = now;
            delivery.LastError = null;
            destination.ConsecutiveFailures = 0;
            destination.CircuitOpenUntil = null;
            return;
        }

        if (result.StatusCode == 410)
        {
            delivery.Status = DeliveryStatus.DeadLettered;
            delivery.CompletedAt = now;
            delivery.LastError = "The destination answered 410 Gone, so it was switched off.";
            destination.Enabled = false;
            destination.DisabledReason = "Answered 410 Gone.";
            return;
        }

        if (result.StatusCode is >= 400 and < 500 && result.StatusCode is not (408 or 429))
        {
            // The server is alive and has said no. Retrying the same request cannot change its mind.
            delivery.Status = DeliveryStatus.DeadLettered;
            delivery.CompletedAt = now;
            delivery.LastError = $"Rejected with {result.StatusCode}. Not retried.";
            destination.ConsecutiveFailures = 0;
            destination.CircuitOpenUntil = null;
            return;
        }

        destination.ConsecutiveFailures++;
        if (destination.ConsecutiveFailures >= _options.CircuitThreshold)
        {
            destination.CircuitOpenUntil = now + _options.CircuitOpenFor;
        }

        if (delivery.Attempts >= _options.MaxAttempts)
        {
            delivery.Status = DeliveryStatus.DeadLettered;
            delivery.CompletedAt = now;
            delivery.LastError = $"Gave up after {delivery.Attempts} attempts. Last result: {Describe(result)}.";
            return;
        }

        var wait = _options.Backoff[Math.Min(delivery.Attempts - 1, _options.Backoff.Length - 1)];
        if (result.RetryAfter is { } asked)
        {
            wait = TimeSpan.FromTicks(Math.Max(wait.Ticks, Math.Min(asked.Ticks, _options.MaxRetryAfter.Ticks)));
        }

        delivery.Status = DeliveryStatus.Retrying;
        delivery.NextAttemptAt = now + wait;
        delivery.LastError = Describe(result);
    }

    private static string Describe(TransportResult result) => result.StatusCode is { } code ? $"HTTP {code}" : result.Error ?? "No answer";

    // ---- Replay ----

    /// <summary>Sends a fresh copy of a finished delivery. The original stays as it was, so the history is never rewritten.</summary>
    public Created<Delivery> ReplayDelivery(Guid id)
    {
        lock (_gate)
        {
            var original = _deliveries.FirstOrDefault(d => d.Id == id);
            if (original is null)
            {
                return new(false, "No such delivery.", null);
            }

            if (original.IsOpen)
            {
                return new(false, "This delivery is still being tried. Replay applies to finished ones.", null);
            }

            if (!_destinations.ContainsKey(original.DestinationId))
            {
                return new(false, "The destination no longer exists.", null);
            }

            if (!_events.TryGetValue(original.EventId, out var inbound))
            {
                return new(false, "The event is no longer kept.", null);
            }

            original.Replayed = true;
            return new(true, "Replay queued.", Queue(inbound, original.DestinationId, original.RouteId, original.Id).Snapshot());
        }
    }

    /// <summary>Routes a kept event again, to the current routes or to one chosen destination.</summary>
    public Created<IReadOnlyList<Delivery>> ReplayEvent(Guid eventId, Guid? destinationId = null)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out var inbound))
            {
                return new(false, "No such event.", null);
            }

            var created = new List<Delivery>();
            if (destinationId is { } only)
            {
                if (!_destinations.ContainsKey(only))
                {
                    return new(false, "No such destination.", null);
                }

                created.Add(Queue(inbound, only, null, null));
            }
            else
            {
                using var document = JsonDocument.Parse(inbound.Body);
                foreach (var route in _routes.Where(r => r.SourceId == inbound.SourceId && r.Enabled))
                {
                    if (_destinations.ContainsKey(route.DestinationId) && created.All(c => c.DestinationId != route.DestinationId) && Matching.TypeMatches(route.EventTypes, inbound.Type) && Matching.ConditionMatches(route.Where, document.RootElement))
                    {
                        created.Add(Queue(inbound, route.DestinationId, route.Id, null));
                    }
                }
            }

            return new(true, created.Count == 0 ? "No route wants this event." : $"Queued {created.Count} delivery(ies).", created.Select(d => d.Snapshot()).ToList());
        }
    }

    /// <summary>Replays every dead-lettered delivery of a destination once. Calling it again does not queue them a second time.</summary>
    public int ReplayDeadLetters(Guid destinationId)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var original in _deliveries.Where(d => d.DestinationId == destinationId && d.Status == DeliveryStatus.DeadLettered && !d.Replayed).ToList())
            {
                if (!_events.TryGetValue(original.EventId, out var inbound))
                {
                    continue;
                }

                original.Replayed = true;
                Queue(inbound, destinationId, original.RouteId, original.Id);
                count++;
            }

            return count;
        }
    }

    // ---- Reading ----

    public IReadOnlyList<InboundEvent> Events(Guid? sourceId = null, int limit = 100)
    {
        lock (_gate)
        {
            return _eventOrder.AsEnumerable().Reverse().Select(id => _events[id]).Where(e => sourceId is null || e.SourceId == sourceId).Take(Math.Clamp(limit, 1, 500)).ToList();
        }
    }

    public InboundEvent? Event(Guid id)
    {
        lock (_gate)
        {
            return _events.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<Delivery> Deliveries(DeliveryStatus? status = null, Guid? destinationId = null, Guid? eventId = null, int limit = 200)
    {
        lock (_gate)
        {
            return _deliveries
                .Where(d => (status is null || d.Status == status) && (destinationId is null || d.DestinationId == destinationId) && (eventId is null || d.EventId == eventId))
                .OrderByDescending(d => d.CreatedAt)
                .Take(Math.Clamp(limit, 1, 1000))
                .Select(d => d.Snapshot())
                .ToList();
        }
    }

    public Delivery? Delivery(Guid id)
    {
        lock (_gate)
        {
            return _deliveries.FirstOrDefault(d => d.Id == id)?.Snapshot();
        }
    }

    public IReadOnlyList<DestinationHealth> Health()
    {
        lock (_gate)
        {
            var now = Now;
            return _destinations.Values.OrderBy(d => d.Name).Select(d =>
            {
                var mine = _deliveries.Where(x => x.DestinationId == d.Id).ToList();
                var circuit = d.CircuitOpenUntil is { } until && until > now ? CircuitState.Open : d.ConsecutiveFailures >= _options.CircuitThreshold ? CircuitState.HalfOpen : CircuitState.Closed;
                return new DestinationHealth(d.Id, d.Name, d.Enabled, circuit, d.ConsecutiveFailures, mine.Count(x => x.IsOpen), mine.Count(x => x.Status == DeliveryStatus.DeadLettered), mine.Count(x => x.Status == DeliveryStatus.Succeeded));
            }).ToList();
        }
    }
}
