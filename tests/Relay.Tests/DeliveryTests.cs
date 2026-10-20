using Relay.Engine;

namespace Relay.Tests;

public class DeliveryTests
{
    private readonly Rig _r = new();

    private Delivery Queued(string id = "evt_1")
    {
        _r.Post(id);
        return _r.Hub.Deliveries().First(d => _r.Hub.Event(d.EventId)!.ProviderEventId == id);
    }

    [Fact]
    public void A_due_delivery_is_sent_and_marked_succeeded()
    {
        var d = Queued();

        var attempts = _r.Run();

        Assert.Equal(1, attempts);
        var done = _r.Hub.Delivery(d.Id)!;
        Assert.Equal(DeliveryStatus.Succeeded, done.Status);
        Assert.Equal(1, done.Attempts);
        Assert.Equal(200, done.LastStatusCode);
        Assert.Equal(_r.Clock.GetUtcNow(), done.CompletedAt);
    }

    [Fact]
    public void The_destination_gets_the_original_body_and_headers_that_identify_the_delivery()
    {
        var d = Queued();

        _r.Run();

        var sent = Assert.Single(_r.Transport.Sent);
        var inbound = _r.Hub.Event(d.EventId)!;
        Assert.Equal(inbound.Body, sent.Body);
        Assert.Equal(_r.Billing.Id, sent.Destination.Id);
        Assert.Equal(inbound.Id.ToString(), sent.Headers[Headers.EventId]);
        Assert.Equal(d.Id.ToString(), sent.Headers[Headers.DeliveryId]);
        Assert.Equal("1", sent.Headers[Headers.Attempt]);
        Assert.Equal("invoice.paid", sent.Headers[Headers.EventType]);
    }

    [Fact]
    public void The_relay_signs_what_it_sends_so_the_destination_can_verify_it()
    {
        var d = Queued();

        _r.Run();

        var sent = _r.Transport.Sent.Single();
        var result = Signing.VerifyOutbound(sent.Headers[Headers.Signature], d.EventId, sent.Body, _r.Billing.Secret, _r.Clock.GetUtcNow(), TimeSpan.FromMinutes(5));
        Assert.Equal(VerifyResult.Valid, result);
        Assert.Equal(VerifyResult.Mismatch, Signing.VerifyOutbound(sent.Headers[Headers.Signature], d.EventId, sent.Body, _r.Audit.Secret, _r.Clock.GetUtcNow(), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Each_attempt_is_signed_afresh_so_a_late_retry_is_not_stale()
    {
        Queued();
        _r.Transport.Handler = _ => Rig.Down;
        _r.Run();
        _r.Skip(TimeSpan.FromMinutes(1));
        _r.Run();

        var first = _r.Transport.Sent[0].Headers[Headers.Signature];
        var second = _r.Transport.Sent[1].Headers[Headers.Signature];

        Assert.NotEqual(first, second);
        Assert.Equal("2", _r.Transport.Sent[1].Headers[Headers.Attempt]);
        var eventId = _r.Hub.Deliveries().Single().EventId;
        Assert.Equal(VerifyResult.Valid, Signing.VerifyOutbound(second, eventId, _r.Transport.Sent[1].Body, _r.Billing.Secret, _r.Clock.GetUtcNow(), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Nothing_is_sent_when_nothing_is_due()
    {
        Assert.Equal(0, _r.Run());
        Assert.Empty(_r.Transport.Sent);
    }

    [Fact]
    public void A_succeeded_delivery_is_never_sent_again()
    {
        Queued();
        _r.Run();

        _r.Skip(TimeSpan.FromDays(1));

        Assert.Equal(0, _r.Run());
        Assert.Single(_r.Transport.Sent);
    }

    [Fact]
    public void A_failed_attempt_is_retried_after_one_minute_then_five_thirty_two_hours_and_six_hours()
    {
        var d = Queued();
        _r.Transport.Handler = _ => Rig.Down;
        var waits = new[] { 1, 5, 30, 120, 360 };

        foreach (var minutes in waits)
        {
            var before = _r.Clock.GetUtcNow();
            _r.Run();
            var now = _r.Hub.Delivery(d.Id)!;
            Assert.Equal(DeliveryStatus.Retrying, now.Status);
            Assert.Equal(before.AddMinutes(minutes), now.NextAttemptAt);
            Assert.Equal(0, _r.Run());
            _r.Skip(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            Assert.Equal(0, _r.Run());
            _r.Skip(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void After_six_failed_attempts_the_delivery_is_dead_lettered_with_the_reason()
    {
        var d = Queued();
        _r.Transport.Handler = _ => Rig.Down;

        for (var i = 0; i < 6; i++)
        {
            _r.Run();
            _r.Skip(TimeSpan.FromHours(7));
        }

        var dead = _r.Hub.Delivery(d.Id)!;
        Assert.Equal(DeliveryStatus.DeadLettered, dead.Status);
        Assert.Equal(6, dead.Attempts);
        Assert.Contains("Gave up after 6 attempts", dead.LastError);
        Assert.Contains("503", dead.LastError);
        Assert.NotNull(dead.CompletedAt);
        Assert.Equal(0, _r.Run());
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(408)]
    [InlineData(429)]
    public void Server_errors_timeouts_and_rate_limits_are_retried(int status)
    {
        var d = Queued();
        _r.Transport.Handler = _ => new TransportResult(status);

        _r.Run();

        Assert.Equal(DeliveryStatus.Retrying, _r.Hub.Delivery(d.Id)!.Status);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public void A_client_error_is_final_because_retrying_the_same_request_cannot_change_the_answer(int status)
    {
        var d = Queued();
        _r.Transport.Handler = _ => new TransportResult(status);

        _r.Run();

        var dead = _r.Hub.Delivery(d.Id)!;
        Assert.Equal(DeliveryStatus.DeadLettered, dead.Status);
        Assert.Equal(1, dead.Attempts);
        Assert.Contains($"Rejected with {status}", dead.LastError);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    public void A_redirect_is_not_a_success(int status)
    {
        var d = Queued();
        _r.Transport.Handler = _ => new TransportResult(status);

        _r.Run();

        Assert.NotEqual(DeliveryStatus.Succeeded, _r.Hub.Delivery(d.Id)!.Status);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    public void Any_2xx_is_a_success(int status)
    {
        var d = Queued();
        _r.Transport.Handler = _ => new TransportResult(status);

        _r.Run();

        Assert.Equal(DeliveryStatus.Succeeded, _r.Hub.Delivery(d.Id)!.Status);
    }

    [Fact]
    public void A_timeout_with_no_status_is_retried_and_the_reason_is_kept()
    {
        var d = Queued();
        _r.Transport.Handler = _ => new TransportResult(null, "Timed out");

        _r.Run();

        var now = _r.Hub.Delivery(d.Id)!;
        Assert.Equal(DeliveryStatus.Retrying, now.Status);
        Assert.Equal("Timed out", now.LastError);
        Assert.Null(now.LastStatusCode);
    }

    [Fact]
    public void A_transport_that_throws_counts_as_a_failed_attempt_and_does_not_stop_other_deliveries()
    {
        var first = Queued("a");
        var second = Queued("b");
        _r.Transport.Handler = request => request.DeliveryId == first.Id ? throw new InvalidOperationException("boom") : Rig.Ok;

        _r.Run();

        Assert.Equal(DeliveryStatus.Retrying, _r.Hub.Delivery(first.Id)!.Status);
        Assert.Equal("boom", _r.Hub.Delivery(first.Id)!.LastError);
        Assert.Equal(DeliveryStatus.Succeeded, _r.Hub.Delivery(second.Id)!.Status);
    }

    [Fact]
    public void A_410_dead_letters_the_delivery_and_switches_the_destination_off()
    {
        var d = Queued();
        _r.Transport.Handler = _ => new TransportResult(410);

        _r.Run();

        Assert.Equal(DeliveryStatus.DeadLettered, _r.Hub.Delivery(d.Id)!.Status);
        var destination = _r.Hub.Destination(_r.Billing.Id)!;
        Assert.False(destination.Enabled);
        Assert.Contains("410", destination.DisabledReason);
    }

    [Fact]
    public void Other_deliveries_for_a_destination_that_said_gone_are_held_not_lost_and_go_out_once_it_is_back()
    {
        var first = Queued("a");
        var second = Queued("b");
        var third = Queued("c");
        _r.Transport.Handler = request => request.DeliveryId == first.Id ? new TransportResult(410) : Rig.Ok;

        _r.Run();

        var states = new[] { first, second, third }.Select(x => _r.Hub.Delivery(x.Id)!.Status).ToList();
        Assert.Equal(1, states.Count(s => s == DeliveryStatus.DeadLettered));
        Assert.Equal(2, states.Count(s => s == DeliveryStatus.Pending));

        _r.Transport.Handler = _ => Rig.Ok;
        _r.Hub.SetDestinationEnabled(_r.Billing.Id, true);
        _r.Run();

        Assert.Equal(2, _r.Hub.Deliveries(DeliveryStatus.Succeeded).Count);
    }

    [Fact]
    public void A_disabled_destination_holds_its_deliveries()
    {
        Queued();
        _r.Hub.SetDestinationEnabled(_r.Billing.Id, false);

        Assert.Equal(0, _r.Run());
        Assert.Equal(DeliveryStatus.Pending, _r.Only().Status);

        _r.Hub.SetDestinationEnabled(_r.Billing.Id, true);
        Assert.Equal(1, _r.Run());
    }

    [Fact]
    public void Retry_after_can_make_the_wait_longer()
    {
        var d = Queued();
        _r.Transport.Handler = _ => new TransportResult(429, null, TimeSpan.FromMinutes(20));
        var before = _r.Clock.GetUtcNow();

        _r.Run();

        Assert.Equal(before.AddMinutes(20), _r.Hub.Delivery(d.Id)!.NextAttemptAt);
    }

    [Fact]
    public void Retry_after_can_never_make_the_wait_shorter_than_the_schedule()
    {
        var d = Queued();
        _r.Transport.Handler = _ => new TransportResult(503, null, TimeSpan.FromSeconds(5));
        var before = _r.Clock.GetUtcNow();

        _r.Run();

        Assert.Equal(before.AddMinutes(1), _r.Hub.Delivery(d.Id)!.NextAttemptAt);
    }

    [Fact]
    public void Retry_after_is_capped_at_a_day()
    {
        var d = Queued();
        _r.Transport.Handler = _ => new TransportResult(429, null, TimeSpan.FromDays(30));
        var before = _r.Clock.GetUtcNow();

        _r.Run();

        Assert.Equal(before.AddHours(24), _r.Hub.Delivery(d.Id)!.NextAttemptAt);
    }

    [Fact]
    public void A_delivery_that_succeeds_after_failures_keeps_its_whole_history()
    {
        var d = Queued();
        _r.Transport.Handler = _ => new TransportResult(503);
        _r.Run();
        _r.Skip(TimeSpan.FromMinutes(1));
        _r.Transport.Handler = _ => new TransportResult(null, "Timed out");
        _r.Run();
        _r.Skip(TimeSpan.FromMinutes(5));
        _r.Transport.Handler = _ => Rig.Ok;
        _r.Run();

        var done = _r.Hub.Delivery(d.Id)!;
        Assert.Equal(DeliveryStatus.Succeeded, done.Status);
        Assert.Equal([1, 2, 3], done.History.Select(a => a.Number));
        Assert.Equal([503, null, 200], done.History.Select(a => a.StatusCode));
        Assert.Equal("Timed out", done.History[1].Error);
        Assert.Null(done.LastError);
        Assert.Equal(done.Attempts, done.History.Count);
    }

    [Fact]
    public void One_destination_failing_does_not_hold_up_another()
    {
        _r.Hub.AddRoute(_r.Source.Id, _r.Audit.Id);
        _r.Post("evt_1");
        _r.Transport.Handler = request => request.Destination.Id == _r.Billing.Id ? Rig.Down : Rig.Ok;

        _r.Run();

        Assert.Equal(DeliveryStatus.Retrying, _r.Hub.Deliveries().Single(x => x.DestinationId == _r.Billing.Id).Status);
        Assert.Equal(DeliveryStatus.Succeeded, _r.Hub.Deliveries().Single(x => x.DestinationId == _r.Audit.Id).Status);
    }

    [Fact]
    public void Due_deliveries_go_out_oldest_first()
    {
        var a = Queued("a");
        _r.Skip(TimeSpan.FromSeconds(1));
        var b = Queued("b");
        _r.Skip(TimeSpan.FromSeconds(1));
        var c = Queued("c");

        _r.Run();

        Assert.Equal([a.Id, b.Id, c.Id], _r.Transport.Sent.Select(s => s.DeliveryId));
    }

    [Fact]
    public async Task Parallel_runs_attempt_each_delivery_exactly_once()
    {
        for (var i = 0; i < 30; i++)
        {
            Queued("e" + i);
        }

        _r.Transport.Handler = _ =>
        {
            Thread.Sleep(2);
            return Rig.Ok;
        };

        var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _r.Hub.ProcessDueAsync(_r.Transport))));

        Assert.Equal(30, counts.Sum());
        Assert.Equal(30, _r.Transport.Sent.Count);
        Assert.Equal(30, _r.Transport.Sent.Select(s => s.DeliveryId).Distinct().Count());
        Assert.All(_r.Hub.Deliveries(limit: 100), d => Assert.Equal(1, d.Attempts));
    }

    [Fact]
    public async Task A_cancelled_run_releases_the_delivery_so_the_next_run_sends_it()
    {
        var d = Queued();
        using var cts = new CancellationTokenSource();
        var cancelling = new CancellingTransport(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _r.Hub.ProcessDueAsync(cancelling, cts.Token));

        Assert.Equal(0, _r.Hub.Delivery(d.Id)!.Attempts);
        Assert.Equal(1, _r.Run());
        Assert.Equal(DeliveryStatus.Succeeded, _r.Hub.Delivery(d.Id)!.Status);
    }

    private sealed class CancellingTransport(CancellationTokenSource source) : ITransport
    {
        public Task<TransportResult> SendAsync(OutboundRequest request, CancellationToken ct)
        {
            source.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new TransportResult(200));
        }
    }
}
