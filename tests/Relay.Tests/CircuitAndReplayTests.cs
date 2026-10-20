using Relay.Engine;

namespace Relay.Tests;

public class CircuitBreakerTests
{
    private readonly Rig _r = new();

    private void Events(int count, string prefix = "e")
    {
        for (var i = 0; i < count; i++)
        {
            _r.Post(prefix + i);
        }
    }

    private CircuitState State() => _r.Hub.Health().Single(h => h.DestinationId == _r.Billing.Id).Circuit;

    [Fact]
    public void Five_failures_in_a_row_open_the_circuit()
    {
        Events(5);
        _r.Transport.Handler = _ => Rig.Down;

        _r.Run();

        Assert.Equal(CircuitState.Open, State());
        Assert.Equal(_r.Clock.GetUtcNow().AddMinutes(10), _r.Hub.Destination(_r.Billing.Id)!.CircuitOpenUntil);
    }

    [Fact]
    public void Four_failures_are_not_enough()
    {
        Events(4);
        _r.Transport.Handler = _ => Rig.Down;

        _r.Run();

        Assert.Equal(CircuitState.Closed, State());
    }

    [Fact]
    public void Once_it_opens_in_the_middle_of_a_run_the_rest_of_that_run_is_not_sent()
    {
        Events(9);
        _r.Transport.Handler = _ => Rig.Down;

        var attempted = _r.Run();

        Assert.Equal(5, attempted);
        Assert.Equal(5, _r.Transport.Sent.Count);
        Assert.Equal(4, _r.Hub.Deliveries(DeliveryStatus.Pending).Count);
    }

    [Fact]
    public void While_the_circuit_is_open_nothing_is_sent_and_waiting_does_not_use_up_attempts()
    {
        Events(6);
        _r.Transport.Handler = _ => Rig.Down;
        _r.Run();
        var sent = _r.Transport.Sent.Count;

        _r.Skip(TimeSpan.FromMinutes(9));
        Assert.Equal(0, _r.Run());

        Assert.Equal(sent, _r.Transport.Sent.Count);
        Assert.All(_r.Hub.Deliveries(), d => Assert.True(d.Attempts <= 1));
    }

    [Fact]
    public void After_the_cool_off_exactly_one_delivery_is_let_through_as_a_probe()
    {
        Events(8);
        _r.Transport.Handler = _ => Rig.Down;
        _r.Run();
        var before = _r.Transport.Sent.Count;

        _r.Skip(TimeSpan.FromMinutes(10));
        var attempted = _r.Run();

        Assert.Equal(1, attempted);
        Assert.Equal(before + 1, _r.Transport.Sent.Count);
    }

    [Fact]
    public void A_failed_probe_opens_the_circuit_again_for_another_ten_minutes()
    {
        Events(6);
        _r.Transport.Handler = _ => Rig.Down;
        _r.Run();
        _r.Skip(TimeSpan.FromMinutes(10));

        _r.Run();

        Assert.Equal(CircuitState.Open, State());
        Assert.Equal(_r.Clock.GetUtcNow().AddMinutes(10), _r.Hub.Destination(_r.Billing.Id)!.CircuitOpenUntil);
    }

    [Fact]
    public void A_successful_probe_closes_the_circuit_and_the_rest_flow_on_the_next_run()
    {
        Events(8);
        _r.Transport.Handler = _ => Rig.Down;
        _r.Run();
        _r.Skip(TimeSpan.FromMinutes(10));
        _r.Transport.Handler = _ => Rig.Ok;

        Assert.Equal(1, _r.Run());
        Assert.Equal(CircuitState.Closed, State());
        Assert.Equal(0, _r.Hub.Destination(_r.Billing.Id)!.ConsecutiveFailures);

        _r.Run();

        Assert.Equal(8, _r.Hub.Deliveries(DeliveryStatus.Succeeded).Count + _r.Hub.Deliveries(DeliveryStatus.Retrying).Count);
        Assert.True(_r.Hub.Deliveries(DeliveryStatus.Succeeded).Count >= 4);
    }

    [Fact]
    public void A_success_in_between_resets_the_count()
    {
        _r.Transport.Handler = _ => Rig.Down;
        Events(4);
        _r.Run();
        _r.Transport.Handler = _ => Rig.Ok;
        _r.Post("good");
        _r.Run();
        Assert.Equal(0, _r.Hub.Destination(_r.Billing.Id)!.ConsecutiveFailures);

        _r.Transport.Handler = _ => Rig.Down;
        Events(4, "again");
        _r.Run();

        Assert.Equal(CircuitState.Closed, State());
    }

    [Fact]
    public void A_permanent_client_error_does_not_count_because_the_server_is_alive()
    {
        Events(8);
        _r.Transport.Handler = _ => new TransportResult(400);

        _r.Run();

        Assert.Equal(CircuitState.Closed, State());
        Assert.Equal(0, _r.Hub.Destination(_r.Billing.Id)!.ConsecutiveFailures);
        Assert.Equal(8, _r.Transport.Sent.Count);
    }

    [Fact]
    public void Each_destination_has_its_own_breaker()
    {
        _r.Hub.AddRoute(_r.Source.Id, _r.Audit.Id);
        Events(6);
        _r.Transport.Handler = request => request.Destination.Id == _r.Billing.Id ? Rig.Down : Rig.Ok;

        _r.Run();

        var health = _r.Hub.Health();
        Assert.Equal(CircuitState.Open, health.Single(h => h.DestinationId == _r.Billing.Id).Circuit);
        Assert.Equal(CircuitState.Closed, health.Single(h => h.DestinationId == _r.Audit.Id).Circuit);
        Assert.Equal(6, health.Single(h => h.DestinationId == _r.Audit.Id).Succeeded);
    }

    [Fact]
    public void Health_reports_half_open_once_the_cool_off_has_passed_and_counts_each_state()
    {
        Events(6);
        _r.Transport.Handler = _ => Rig.Down;
        _r.Run();
        Assert.Equal(CircuitState.Open, State());

        _r.Skip(TimeSpan.FromMinutes(10));

        var health = _r.Hub.Health().Single(h => h.DestinationId == _r.Billing.Id);
        Assert.Equal(CircuitState.HalfOpen, health.Circuit);
        Assert.Equal(6, health.Pending);
        Assert.Equal(0, health.Succeeded);
    }

    [Fact]
    public void Switching_a_destination_back_on_by_hand_closes_the_circuit()
    {
        Events(6);
        _r.Transport.Handler = _ => Rig.Down;
        _r.Run();
        _r.Hub.SetDestinationEnabled(_r.Billing.Id, false);

        _r.Hub.SetDestinationEnabled(_r.Billing.Id, true);

        Assert.Equal(CircuitState.Closed, State());
        Assert.Equal(0, _r.Hub.Destination(_r.Billing.Id)!.ConsecutiveFailures);
    }

    [Fact]
    public void The_threshold_and_cool_off_are_configurable()
    {
        var r = new Rig(new RelayOptions { CircuitThreshold = 2, CircuitOpenFor = TimeSpan.FromMinutes(1) });
        r.Post("a");
        r.Post("b");
        r.Transport.Handler = _ => Rig.Down;

        r.Run();

        Assert.Equal(CircuitState.Open, r.Hub.Health().Single(h => h.DestinationId == r.Billing.Id).Circuit);
        r.Skip(TimeSpan.FromMinutes(1));
        Assert.Equal(CircuitState.HalfOpen, r.Hub.Health().Single(h => h.DestinationId == r.Billing.Id).Circuit);
    }
}

public class ReplayTests
{
    private readonly Rig _r = new();

    private Delivery DeadLetter(string id = "evt_1")
    {
        _r.Post(id);
        _r.Transport.Handler = _ => new TransportResult(400);
        _r.Run();
        _r.Transport.Handler = _ => Rig.Ok;
        return _r.Hub.Deliveries(DeliveryStatus.DeadLettered).First();
    }

    [Fact]
    public void Replaying_a_dead_letter_queues_a_fresh_delivery_and_leaves_the_original_alone()
    {
        var dead = DeadLetter();

        var replay = _r.Hub.ReplayDelivery(dead.Id);

        Assert.True(replay.Ok);
        Assert.Equal(DeliveryStatus.Pending, replay.Value!.Status);
        Assert.Equal(0, replay.Value.Attempts);
        Assert.Equal(dead.Id, replay.Value.ReplayOf);
        Assert.Equal(dead.EventId, replay.Value.EventId);
        Assert.Equal(DeliveryStatus.DeadLettered, _r.Hub.Delivery(dead.Id)!.Status);
        Assert.Equal(1, _r.Hub.Delivery(dead.Id)!.Attempts);
    }

    [Fact]
    public void A_replay_is_delivered_and_signed_afresh()
    {
        var dead = DeadLetter();
        _r.Skip(TimeSpan.FromHours(1));
        var replay = _r.Hub.ReplayDelivery(dead.Id).Value!;

        _r.Run();

        Assert.Equal(DeliveryStatus.Succeeded, _r.Hub.Delivery(replay.Id)!.Status);
        var sent = _r.Transport.Sent.Last();
        Assert.Equal(replay.Id.ToString(), sent.Headers[Headers.DeliveryId]);
        Assert.Equal("1", sent.Headers[Headers.Attempt]);
        Assert.Equal(VerifyResult.Valid, Signing.VerifyOutbound(sent.Headers[Headers.Signature], dead.EventId, sent.Body, _r.Billing.Secret, _r.Clock.GetUtcNow(), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_delivered_event_can_be_replayed_on_purpose()
    {
        _r.Post("evt_1");
        _r.Run();
        var done = _r.Only();

        Assert.True(_r.Hub.ReplayDelivery(done.Id).Ok);
        _r.Run();

        Assert.Equal(2, _r.Hub.Deliveries(DeliveryStatus.Succeeded).Count);
    }

    [Fact]
    public void A_delivery_still_being_tried_cannot_be_replayed()
    {
        _r.Post("evt_1");
        var open = _r.Only();

        var result = _r.Hub.ReplayDelivery(open.Id);

        Assert.False(result.Ok);
        Assert.Contains("still being tried", result.Message);
    }

    [Fact]
    public void Replaying_something_that_does_not_exist_or_whose_destination_is_gone_is_refused()
    {
        Assert.False(_r.Hub.ReplayDelivery(Guid.NewGuid()).Ok);
        var dead = DeadLetter();
        _r.Hub.RemoveDestination(_r.Billing.Id);

        var result = _r.Hub.ReplayDelivery(dead.Id);

        Assert.False(result.Ok);
        Assert.Contains("no longer exists", result.Message);
    }

    [Fact]
    public void An_event_can_be_routed_again_to_the_current_routes()
    {
        _r.Post("evt_1");
        _r.Run();
        var inbound = _r.Hub.Events().Single();
        _r.Hub.AddRoute(_r.Source.Id, _r.Audit.Id);

        var result = _r.Hub.ReplayEvent(inbound.Id);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal(new[] { _r.Audit.Id, _r.Billing.Id }.Order(), result.Value.Select(d => d.DestinationId).Order());
    }

    [Fact]
    public void Routing_an_event_again_still_sends_it_to_each_destination_once()
    {
        _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id, ["invoice.*"]);
        _r.Post("evt_1");

        var result = _r.Hub.ReplayEvent(_r.Hub.Events().Single().Id);

        Assert.Single(result.Value!);
    }

    [Fact]
    public void An_event_can_be_sent_to_one_chosen_destination_whatever_the_routes_say()
    {
        _r.Post("evt_1");
        var inbound = _r.Hub.Events().Single();

        var result = _r.Hub.ReplayEvent(inbound.Id, _r.Audit.Id);

        Assert.Equal([_r.Audit.Id], result.Value!.Select(d => d.DestinationId));
    }

    [Fact]
    public void Replaying_an_event_that_no_route_wants_says_so()
    {
        _r.Post("evt_1");
        _r.Hub.RemoveRoute(_r.Hub.Routes().Single().Id);

        var result = _r.Hub.ReplayEvent(_r.Hub.Events().Single().Id);

        Assert.True(result.Ok);
        Assert.Empty(result.Value!);
        Assert.Contains("No route", result.Message);
    }

    [Fact]
    public void Replaying_an_unknown_event_or_to_an_unknown_destination_is_refused()
    {
        _r.Post("evt_1");

        Assert.False(_r.Hub.ReplayEvent(Guid.NewGuid()).Ok);
        Assert.False(_r.Hub.ReplayEvent(_r.Hub.Events().Single().Id, Guid.NewGuid()).Ok);
    }

    [Fact]
    public void Replaying_all_dead_letters_of_a_destination_queues_each_once_even_if_asked_twice()
    {
        for (var i = 0; i < 4; i++)
        {
            _r.Post("e" + i);
        }

        _r.Transport.Handler = _ => new TransportResult(422);
        _r.Run();
        _r.Transport.Handler = _ => Rig.Ok;

        Assert.Equal(4, _r.Hub.ReplayDeadLetters(_r.Billing.Id));
        Assert.Equal(0, _r.Hub.ReplayDeadLetters(_r.Billing.Id));
        _r.Run();

        Assert.Equal(4, _r.Hub.Deliveries(DeliveryStatus.Succeeded).Count);
        Assert.Equal(4, _r.Hub.Deliveries(DeliveryStatus.DeadLettered).Count);
    }

    [Fact]
    public void Replaying_all_dead_letters_only_touches_that_destination_and_only_dead_ones()
    {
        _r.Hub.AddRoute(_r.Source.Id, _r.Audit.Id);
        _r.Post("e1");
        _r.Transport.Handler = request => request.Destination.Id == _r.Billing.Id ? new TransportResult(400) : Rig.Ok;
        _r.Run();
        _r.Transport.Handler = _ => Rig.Ok;

        Assert.Equal(0, _r.Hub.ReplayDeadLetters(_r.Audit.Id));
        Assert.Equal(1, _r.Hub.ReplayDeadLetters(_r.Billing.Id));
    }

    [Fact]
    public void Replays_to_a_destination_that_is_off_are_held_until_it_is_back()
    {
        var dead = DeadLetter();
        var replay = _r.Hub.ReplayDelivery(dead.Id).Value!;
        _r.Hub.SetDestinationEnabled(_r.Billing.Id, false);

        Assert.Equal(0, _r.Run());
        Assert.Equal(DeliveryStatus.Pending, _r.Hub.Delivery(replay.Id)!.Status);
    }

    [Fact]
    public void A_replay_of_a_replay_chains_back_to_the_one_before()
    {
        var dead = DeadLetter();
        var first = _r.Hub.ReplayDelivery(dead.Id).Value!;
        _r.Transport.Handler = _ => new TransportResult(400);
        _r.Run();
        _r.Transport.Handler = _ => Rig.Ok;

        var second = _r.Hub.ReplayDelivery(first.Id).Value!;

        Assert.Equal(first.Id, second.ReplayOf);
    }
}

public class RetentionTests
{
    [Fact]
    public void The_oldest_events_are_dropped_with_their_finished_deliveries_past_the_limit()
    {
        var r = new Rig();
        var first = r.Post("e0");
        r.Run();
        for (var i = 1; i < RelayHub.MaxEventsKept + 20; i++)
        {
            r.Post("e" + i);
            r.Run();
        }

        Assert.Null(r.Hub.Event(first.EventId!.Value));
        Assert.Equal("e" + (RelayHub.MaxEventsKept + 19), r.Hub.Events(limit: 1).Single().ProviderEventId);
        Assert.Equal(RelayHub.MaxEventsKept, r.Hub.Deliveries(limit: 1000).Count);
    }

    [Fact]
    public void An_event_with_a_delivery_still_in_progress_is_kept_while_there_is_room()
    {
        var r = new Rig();
        var first = r.Post("first");
        for (var i = 0; i < RelayHub.MaxEventsKept + 5; i++)
        {
            r.Post("e" + i);
        }

        Assert.NotNull(r.Hub.Event(first.EventId!.Value));
    }

    [Fact]
    public void But_memory_is_still_bounded_when_deliveries_never_finish()
    {
        var r = new Rig();
        var first = r.Post("first");
        for (var i = 0; i < RelayHub.MaxEventsKept * 2 + 5; i++)
        {
            r.Post("e" + i);
        }

        Assert.Null(r.Hub.Event(first.EventId!.Value));
    }

    [Fact]
    public void A_dropped_event_cannot_be_replayed()
    {
        var r = new Rig();
        r.Post("old");
        r.Run();
        var oldDelivery = r.Only();
        for (var i = 0; i < RelayHub.MaxEventsKept + 5; i++)
        {
            r.Post("e" + i);
            r.Run();
        }

        Assert.False(r.Hub.ReplayDelivery(oldDelivery.Id).Ok);
    }
}