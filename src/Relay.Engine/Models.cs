namespace Relay.Engine;

/// <summary>How a provider proves a request is really theirs.</summary>
public enum SignatureScheme
{
    /// <summary>Stripe style: header <c>Stripe-Signature: t=unix,v1=hex</c>, HMAC-SHA256 of "{t}.{body}". Has a timestamp, so old requests are refused.</summary>
    Timestamped,

    /// <summary>GitHub style: header <c>X-Hub-Signature-256: sha256=hex</c>, HMAC-SHA256 of the body. No timestamp, so de-duplication is the replay defense.</summary>
    HmacBody,

    /// <summary>No signature. Only for sources that cannot sign; anyone who knows the url can post.</summary>
    None,
}

public sealed class Source
{
    internal Source Snapshot() => (Source)MemberwiseClone();

    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public SignatureScheme Scheme { get; init; }
    public string Secret { get; internal set; } = "";
    public string? PreviousSecret { get; internal set; }
    public DateTimeOffset? PreviousSecretValidUntil { get; internal set; }

    /// <summary>Where to find the provider's own event id: a dotted JSON path such as <c>id</c>, or <c>header:X-GitHub-Delivery</c>.</summary>
    public string EventIdPath { get; init; } = "id";

    /// <summary>Where to find the event type: a dotted JSON path such as <c>type</c>, or <c>header:X-GitHub-Event</c>.</summary>
    public string EventTypePath { get; init; } = "type";

    /// <summary>A paused source answers 503, so a well-behaved provider keeps retrying instead of losing events.</summary>
    public bool Paused { get; internal set; }

    public int RatePerMinute { get; init; } = 600;
}

public sealed class Destination
{
    internal Destination Snapshot() => (Destination)MemberwiseClone();

    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";

    /// <summary>The secret this destination uses to verify that a delivery really came from the relay.</summary>
    public string Secret { get; internal set; } = "";

    public bool Enabled { get; internal set; } = true;
    public string? DisabledReason { get; internal set; }
    public int ConsecutiveFailures { get; internal set; }
    public DateTimeOffset? CircuitOpenUntil { get; internal set; }
}

/// <summary>Only events matching every part reach the destination.</summary>
public sealed class Route
{
    internal Route Snapshot() => (Route)MemberwiseClone();

    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public Guid DestinationId { get; init; }

    /// <summary>Event types, exact (<c>invoice.paid</c>), prefix (<c>invoice.*</c>) or <c>*</c> for everything.</summary>
    public string[] EventTypes { get; init; } = ["*"];

    /// <summary>Optional: the payload must have this value at this path.</summary>
    public PayloadCondition? Where { get; init; }

    public bool Enabled { get; internal set; } = true;
}

public sealed record PayloadCondition(string Path, string Value);

public sealed class InboundEvent
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public string ProviderEventId { get; init; } = "";
    public string Type { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset ReceivedAt { get; init; }
}

public enum DeliveryStatus
{
    Pending,
    Retrying,
    Succeeded,
    DeadLettered,
}

public sealed record Attempt(int Number, DateTimeOffset At, int? StatusCode, string? Error);

public sealed class Delivery
{
    public Guid Id { get; init; }
    public Guid EventId { get; init; }
    public Guid DestinationId { get; init; }
    public Guid? RouteId { get; init; }
    public Guid? ReplayOf { get; init; }
    public DeliveryStatus Status { get; internal set; }
    public int Attempts { get; internal set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset NextAttemptAt { get; internal set; }
    public DateTimeOffset? CompletedAt { get; internal set; }
    public int? LastStatusCode { get; internal set; }
    public string? LastError { get; internal set; }
    internal List<Attempt> HistoryList { get; set; } = [];
    public IReadOnlyList<Attempt> History => HistoryList;
    internal bool InFlight { get; set; }
    internal bool Replayed { get; set; }

    public bool IsOpen => Status is DeliveryStatus.Pending or DeliveryStatus.Retrying;

    internal Delivery Snapshot()
    {
        var copy = (Delivery)MemberwiseClone();
        copy.HistoryList = [.. HistoryList];
        return copy;
    }
}

public enum IngestOutcome
{
    Accepted,
    Duplicate,
    UnknownSource,
    Unauthorized,
    Expired,
    Paused,
    RateLimited,
    Malformed,
    TooLarge,
}

public sealed record IngestResult(IngestOutcome Outcome, string Message, Guid? EventId = null, int Deliveries = 0)
{
    /// <summary>The HTTP status to answer the provider with.</summary>
    public int StatusCode => Outcome switch
    {
        IngestOutcome.Accepted => 202,
        IngestOutcome.Duplicate => 200,
        IngestOutcome.UnknownSource => 404,
        IngestOutcome.Unauthorized => 401,
        IngestOutcome.Expired => 401,
        IngestOutcome.Paused => 503,
        IngestOutcome.RateLimited => 429,
        IngestOutcome.Malformed => 400,
        IngestOutcome.TooLarge => 413,
        _ => 500,
    };
}

public sealed record Result(bool Ok, string Message)
{
    public static Result Success(string message = "Done.") => new(true, message);
    public static Result Fail(string message) => new(false, message);
}

public sealed record Created<T>(bool Ok, string Message, T? Value) where T : class;

/// <summary>What the transport brings back from one delivery attempt. A null status means no answer (timeout, connection error).</summary>
public sealed record TransportResult(int? StatusCode, string? Error = null, TimeSpan? RetryAfter = null)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

public sealed record OutboundRequest(Destination Destination, Guid DeliveryId, Guid EventId, string EventType, int Attempt, IReadOnlyDictionary<string, string> Headers, string Body);

public interface ITransport
{
    Task<TransportResult> SendAsync(OutboundRequest request, CancellationToken ct);
}

public sealed record RelayOptions
{
    public TimeSpan Tolerance { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxBodyBytes { get; init; } = 256 * 1024;
    public TimeSpan DedupeWindow { get; init; } = TimeSpan.FromDays(7);
    public int MaxAttempts { get; init; } = 6;
    public TimeSpan[] Backoff { get; init; } = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)];
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromHours(24);
    public int CircuitThreshold { get; init; } = 5;
    public TimeSpan CircuitOpenFor { get; init; } = TimeSpan.FromMinutes(10);
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

public sealed record DestinationHealth(Guid DestinationId, string Name, bool Enabled, CircuitState Circuit, int ConsecutiveFailures, int Pending, int DeadLettered, int Succeeded);
