using System.Security.Cryptography;
using Relay.Engine;

namespace Relay.Api;

/// <summary>The real clock plus an offset, so a visitor can skip past a retry delay instead of waiting an hour.</summary>
public sealed class SkewedClock : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public DateTimeOffset Skip(TimeSpan by)
    {
        Interlocked.Add(ref _offsetTicks, by.Ticks);
        return GetUtcNow();
    }
}

/// <summary>One customer: a name, an API key and a private workspace that no other customer can see.</summary>
public sealed class Tenant
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; }
    public string ApiKey { get; } = "rk_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
    public SkewedClock Clock { get; } = new();
    public RelayHub Hub { get; }
    public DemoTransport Transport { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public Tenant(string name)
    {
        Name = name;
        Hub = new RelayHub(Clock);
        Transport = new DemoTransport(Clock);
    }
}

public sealed class TenantRegistry
{
    public const int MaxTenants = 100;

    private readonly object _gate = new();
    private readonly List<Tenant> _tenants = [];

    /// <summary>Creates a workspace. When the demo is full the oldest one is retired, so the service never fills up and refuses visitors.</summary>
    public Tenant Create(string name)
    {
        lock (_gate)
        {
            if (_tenants.Count >= MaxTenants)
            {
                _tenants.RemoveAt(0);
            }

            var tenant = new Tenant(name.Trim());
            _tenants.Add(tenant);
            return tenant;
        }
    }

    public Tenant? ByApiKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        lock (_gate)
        {
            return _tenants.FirstOrDefault(t => CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(t.ApiKey), System.Text.Encoding.UTF8.GetBytes(key)));
        }
    }

    /// <summary>Finds the workspace that owns a source, so the public ingest url does not need an API key.</summary>
    public Tenant? BySource(Guid sourceId)
    {
        lock (_gate)
        {
            return _tenants.FirstOrDefault(t => t.Hub.OwnsSource(sourceId));
        }
    }

    public IReadOnlyList<Tenant> All()
    {
        lock (_gate)
        {
            return _tenants.ToList();
        }
    }
}

public sealed record Received(Guid EventId, Guid DeliveryId, int Attempt, string Type, string Body, bool SignatureValid, DateTimeOffset At);

/// <summary>
/// Stands in for the customer's servers. The demo never makes outbound HTTP calls (that would be a free
/// request-forwarding tool for strangers), so each destination url names a behavior instead:
/// healthy, flaky (fails twice then recovers), slow-down (429 with Retry-After), lost-ack (processes the event but the
/// reply is lost, so it arrives twice), down (always 503), gone (410) and rejects (400).
/// </summary>
public sealed class DemoTransport : ITransport
{
    public static readonly string[] Behaviors = ["healthy", "flaky", "slow-down", "lost-ack", "down", "gone", "rejects"];

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid, Guid), int> _attempts = [];
    private readonly Dictionary<Guid, List<Received>> _inbox = [];

    public DemoTransport(TimeProvider clock) => _clock = clock;

    public static string? BehaviorOf(string url) =>
        url.StartsWith("demo://", StringComparison.OrdinalIgnoreCase) && Behaviors.Contains(url[7..].ToLowerInvariant()) ? url[7..].ToLowerInvariant() : null;

    public IReadOnlyList<Received> Inbox(Guid destinationId)
    {
        lock (_gate)
        {
            return _inbox.TryGetValue(destinationId, out var list) ? list.ToList() : [];
        }
    }

    public Task<TransportResult> SendAsync(OutboundRequest request, CancellationToken ct)
    {
        int attempt;
        lock (_gate)
        {
            var key = (request.Destination.Id, request.EventId);
            _attempts[key] = attempt = _attempts.GetValueOrDefault(key) + 1;
        }

        TransportResult Receive()
        {
            var valid = Signing.VerifyOutbound(request.Headers[Headers.Signature], request.EventId, request.Body, request.Destination.Secret, _clock.GetUtcNow(), TimeSpan.FromMinutes(5)) == VerifyResult.Valid;
            lock (_gate)
            {
                if (!_inbox.TryGetValue(request.Destination.Id, out var list))
                {
                    _inbox[request.Destination.Id] = list = [];
                }

                list.Add(new Received(request.EventId, request.DeliveryId, request.Attempt, request.EventType, request.Body, valid, _clock.GetUtcNow()));
                if (list.Count > 200)
                {
                    list.RemoveAt(0);
                }
            }

            return new TransportResult(200);
        }

        var result = BehaviorOf(request.Destination.Url) switch
        {
            "healthy" => Receive(),
            "flaky" => attempt <= 2 ? new TransportResult(503) : Receive(),
            "slow-down" => attempt == 1 ? new TransportResult(429, null, TimeSpan.FromMinutes(2)) : Receive(),
            "lost-ack" => attempt == 1 ? LoseReply(Receive()) : Receive(),
            "down" => new TransportResult(503),
            "gone" => new TransportResult(410),
            "rejects" => new TransportResult(400),
            _ => new TransportResult(null, "Unknown demo behavior."),
        };
        return Task.FromResult(result);
    }

    // The receiver did its work, but the relay never hears back.
    private static TransportResult LoseReply(TransportResult _) => new(null, "Timed out waiting for the reply.");
}

/// <summary>Runs every workspace's delivery worker in the background, once a second.</summary>
public sealed class DeliveryWorker : BackgroundService
{
    private readonly TenantRegistry _tenants;

    public DeliveryWorker(TenantRegistry tenants) => _tenants = tenants;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var tenant in _tenants.All())
            {
                await tenant.Hub.ProcessDueAsync(tenant.Transport, stoppingToken);
            }
        }
    }
}
