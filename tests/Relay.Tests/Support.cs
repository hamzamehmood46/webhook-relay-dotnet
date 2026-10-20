using System.Text.Json;
using Relay.Engine;

namespace Relay.Tests;

public sealed class SimulatedClock : TimeProvider
{
    private DateTimeOffset _now;

    public SimulatedClock(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public sealed class FakeTransport : ITransport
{
    private readonly object _gate = new();
    private readonly List<OutboundRequest> _sent = [];

    public Func<OutboundRequest, TransportResult> Handler { get; set; } = _ => new TransportResult(200);

    public IReadOnlyList<OutboundRequest> Sent
    {
        get
        {
            lock (_gate)
            {
                return _sent.ToList();
            }
        }
    }

    public Task<TransportResult> SendAsync(OutboundRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            _sent.Add(request);
        }

        return Task.FromResult(Handler(request));
    }
}

/// <summary>A workspace with one Stripe-style source and two healthy destinations, on a clock the test controls.</summary>
public sealed class Rig
{
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public SimulatedClock Clock { get; } = new(Start);
    public RelayHub Hub { get; }
    public FakeTransport Transport { get; } = new();
    public Source Source { get; }
    public Destination Billing { get; }
    public Destination Audit { get; }
    private int _ids;

    public Rig(RelayOptions? options = null, bool routes = true)
    {
        Hub = new RelayHub(Clock, options);
        Source = Hub.AddSource("Payments", SignatureScheme.Timestamped, "whsec_test").Value!;
        Billing = Hub.AddDestination("Billing", "demo://healthy").Value!;
        Audit = Hub.AddDestination("Audit log", "demo://healthy").Value!;
        if (routes)
        {
            Hub.AddRoute(Source.Id, Billing.Id);
        }
    }

    public string Body(string? id = null, string type = "invoice.paid", object? data = null) =>
        JsonSerializer.Serialize(new { id = id ?? "evt_" + ++_ids, type, data = data ?? new { amount = 4200, currency = "usd" } });

    public Dictionary<string, string> Sign(string body, Source? source = null, string? secret = null, long? timestamp = null)
    {
        source ??= Source;
        var (name, value) = Signing.SignInbound(source.Scheme, secret ?? source.Secret, timestamp ?? Clock.GetUtcNow().ToUnixTimeSeconds(), body);
        return string.IsNullOrEmpty(name) ? [] : new Dictionary<string, string> { [name] = value };
    }

    public IngestResult Post(string? id = null, string type = "invoice.paid", object? data = null, Source? source = null, string? secret = null)
    {
        var body = Body(id, type, data);
        return Hub.Ingest((source ?? Source).Id, Sign(body, source, secret), body);
    }

    public IngestResult PostBody(string body, Source? source = null) => Hub.Ingest((source ?? Source).Id, Sign(body, source), body);

    public int Run() => Hub.ProcessDueAsync(Transport).GetAwaiter().GetResult();

    public void Skip(TimeSpan by) => Clock.Advance(by);

    public Delivery Only() => Assert.Single(Hub.Deliveries());

    public static readonly TransportResult Ok = new(200);
    public static readonly TransportResult Down = new(503);
}
