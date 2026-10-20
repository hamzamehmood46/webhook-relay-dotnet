using Relay.Engine;

namespace Relay.Tests;

public class RoutingTests
{
    private readonly Rig _r = new(routes: false);

    private IReadOnlyList<Guid> Destinations() => _r.Hub.Deliveries().Select(d => d.DestinationId).ToList();

    [Fact]
    public void One_event_fans_out_to_every_matching_destination()
    {
        _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id);
        _r.Hub.AddRoute(_r.Source.Id, _r.Audit.Id);

        var result = _r.Post("evt_1");

        Assert.Equal(2, result.Deliveries);
        Assert.Equal(new[] { _r.Audit.Id, _r.Billing.Id }.Order(), Destinations().Order());
    }

    [Fact]
    public void Two_routes_to_the_same_destination_still_deliver_the_event_once()
    {
        _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id, ["invoice.*"]);
        _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id, ["*"]);

        var result = _r.Post("evt_1", "invoice.paid");

        Assert.Equal(1, result.Deliveries);
        Assert.Single(_r.Hub.Deliveries());
    }

    [Fact]
    public void A_route_only_takes_the_event_types_it_asks_for()
    {
        _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id, ["invoice.paid", "invoice.failed"]);
        _r.Hub.AddRoute(_r.Source.Id, _r.Audit.Id, ["customer.*"]);

        Assert.Equal(1, _r.Post("1", "invoice.paid").Deliveries);
        Assert.Equal(1, _r.Post("2", "invoice.failed").Deliveries);
        Assert.Equal(0, _r.Post("3", "invoice.refunded").Deliveries);
        Assert.Equal(1, _r.Post("4", "customer.created").Deliveries);
        Assert.Equal(_r.Audit.Id, _r.Hub.Deliveries(eventId: _r.Hub.Events().First(e => e.ProviderEventId == "4").Id).Single().DestinationId);
    }

    [Fact]
    public void A_payload_condition_filters_by_value()
    {
        _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id, null, new PayloadCondition("data.currency", "usd"));

        Assert.Equal(1, _r.Post("1", data: new { currency = "usd" }).Deliveries);
        Assert.Equal(0, _r.Post("2", data: new { currency = "eur" }).Deliveries);
        Assert.Equal(0, _r.Post("3", data: new { amount = 5 }).Deliveries);
    }

    [Fact]
    public void Type_and_condition_must_both_match()
    {
        _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id, ["invoice.paid"], new PayloadCondition("data.currency", "usd"));

        Assert.Equal(1, _r.Post("1", "invoice.paid", new { currency = "usd" }).Deliveries);
        Assert.Equal(0, _r.Post("2", "invoice.failed", new { currency = "usd" }).Deliveries);
        Assert.Equal(0, _r.Post("3", "invoice.paid", new { currency = "eur" }).Deliveries);
    }

    [Fact]
    public void A_switched_off_route_is_skipped_until_switched_on()
    {
        var route = _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id).Value!;
        _r.Hub.SetRouteEnabled(route.Id, false);
        Assert.Equal(0, _r.Post("1").Deliveries);

        _r.Hub.SetRouteEnabled(route.Id, true);
        Assert.Equal(1, _r.Post("2").Deliveries);
    }

    [Fact]
    public void A_disabled_destination_gets_no_new_deliveries()
    {
        _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id);
        _r.Hub.AddRoute(_r.Source.Id, _r.Audit.Id);
        _r.Hub.SetDestinationEnabled(_r.Audit.Id, false);

        var result = _r.Post("1");

        Assert.Equal(1, result.Deliveries);
        Assert.Equal([_r.Billing.Id], Destinations());
    }

    [Fact]
    public void Routes_belong_to_one_source()
    {
        var other = _r.Hub.AddSource("Other", SignatureScheme.Timestamped, "o").Value!;
        _r.Hub.AddRoute(other.Id, _r.Audit.Id);
        _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id);

        _r.Post("1");

        Assert.Equal([_r.Billing.Id], Destinations());
    }

    [Fact]
    public void A_route_with_no_event_types_means_everything()
    {
        var route = _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id, []).Value!;

        Assert.Equal(["*"], route.EventTypes);
    }

    [Theory]
    [InlineData("invoice.")]
    [InlineData("*.paid")]
    [InlineData("invoice paid")]
    [InlineData("")]
    [InlineData("invoice.**")]
    [InlineData("a/b")]
    public void A_badly_formed_event_type_is_refused(string pattern)
    {
        var result = _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id, [pattern]);

        Assert.False(result.Ok);
        Assert.Contains("not a valid event type", result.Message);
    }

    [Theory]
    [InlineData("invoice.paid")]
    [InlineData("invoice.*")]
    [InlineData("*")]
    [InlineData("customer.subscription.updated")]
    [InlineData("push")]
    [InlineData("pull_request.opened")]
    [InlineData("ns:thing-2.created")]
    public void Well_formed_event_types_are_accepted(string pattern) => Assert.True(_r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id, [pattern]).Ok);

    [Fact]
    public void A_route_needs_a_real_source_and_destination()
    {
        Assert.False(_r.Hub.AddRoute(Guid.NewGuid(), _r.Billing.Id).Ok);
        Assert.False(_r.Hub.AddRoute(_r.Source.Id, Guid.NewGuid()).Ok);
    }

    [Fact]
    public void A_condition_needs_a_path_and_a_value()
    {
        Assert.False(_r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id, null, new PayloadCondition("", "x")).Ok);
        Assert.False(_r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id, null, new PayloadCondition("a", null!)).Ok);
    }

    [Fact]
    public void A_workspace_has_a_route_limit()
    {
        for (var i = 0; i < RelayHub.MaxRoutes; i++)
        {
            Assert.True(_r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id).Ok);
        }

        Assert.False(_r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id).Ok);
    }

    [Fact]
    public void Removing_a_route_stops_it_and_removing_it_twice_fails()
    {
        var route = _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id).Value!;

        Assert.True(_r.Hub.RemoveRoute(route.Id).Ok);
        Assert.False(_r.Hub.RemoveRoute(route.Id).Ok);
        Assert.Equal(0, _r.Post("1").Deliveries);
    }

    [Fact]
    public void Removing_a_destination_removes_its_routes_and_dead_letters_what_was_waiting()
    {
        _r.Hub.AddRoute(_r.Source.Id, _r.Billing.Id);
        _r.Post("1");

        Assert.True(_r.Hub.RemoveDestination(_r.Billing.Id).Ok);

        Assert.Empty(_r.Hub.Routes());
        var waiting = _r.Only();
        Assert.Equal(DeliveryStatus.DeadLettered, waiting.Status);
        Assert.Contains("deleted", waiting.LastError);
        Assert.Equal(0, _r.Run());
    }
}

public class SetupValidationTests
{
    private readonly RelayHub _hub = new(new SimulatedClock(Rig.Start));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_source_needs_a_name(string name) => Assert.False(_hub.AddSource(name, SignatureScheme.Timestamped).Ok);

    [Fact]
    public void A_source_name_is_limited_to_sixty_characters()
    {
        Assert.True(_hub.AddSource(new string('a', 60), SignatureScheme.Timestamped).Ok);
        Assert.False(_hub.AddSource(new string('a', 61), SignatureScheme.Timestamped).Ok);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100_001)]
    public void The_rate_limit_must_be_sensible(int rate) => Assert.False(_hub.AddSource("S", SignatureScheme.Timestamped, ratePerMinute: rate).Ok);

    [Fact]
    public void A_source_needs_somewhere_to_find_the_event_id_and_type()
    {
        Assert.False(_hub.AddSource("S", SignatureScheme.Timestamped, eventIdPath: " ").Ok);
        Assert.False(_hub.AddSource("S", SignatureScheme.Timestamped, eventTypePath: "").Ok);
    }

    [Fact]
    public void A_source_gets_a_generated_secret_unless_it_has_none_or_is_given_one()
    {
        Assert.StartsWith("whsec_", _hub.AddSource("A", SignatureScheme.Timestamped).Value!.Secret);
        Assert.Equal("mine", _hub.AddSource("B", SignatureScheme.HmacBody, " mine ").Value!.Secret);
        Assert.Equal("", _hub.AddSource("C", SignatureScheme.None, "ignored").Value!.Secret);
    }

    [Fact]
    public void A_workspace_has_a_source_limit()
    {
        for (var i = 0; i < RelayHub.MaxSources; i++)
        {
            Assert.True(_hub.AddSource("S" + i, SignatureScheme.Timestamped).Ok);
        }

        Assert.False(_hub.AddSource("one too many", SignatureScheme.Timestamped).Ok);
    }

    [Theory]
    [InlineData("https://example.com/hook", true)]
    [InlineData("http://localhost:5000/hook", true)]
    [InlineData("demo://healthy", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("example.com/hook", false)]
    [InlineData("/relative", false)]
    [InlineData("", false)]
    public void A_destination_url_must_be_an_absolute_http_https_or_demo_address(string url, bool ok) => Assert.Equal(ok, _hub.AddDestination("D", url).Ok);

    [Fact]
    public void A_destination_needs_a_name_and_has_a_limit()
    {
        Assert.False(_hub.AddDestination(" ", "demo://healthy").Ok);
        for (var i = 0; i < RelayHub.MaxDestinations; i++)
        {
            Assert.True(_hub.AddDestination("D" + i, "demo://healthy").Ok);
        }

        Assert.False(_hub.AddDestination("extra", "demo://healthy").Ok);
    }

    [Fact]
    public void Each_destination_has_its_own_secret()
    {
        var a = _hub.AddDestination("A", "demo://healthy").Value!;
        var b = _hub.AddDestination("B", "demo://healthy").Value!;

        Assert.NotEqual(a.Secret, b.Secret);
        Assert.NotEqual(a.Secret, _hub.RotateDestinationSecret(a.Id).Value!.Secret);
    }

    [Fact]
    public void Things_that_do_not_exist_are_reported_not_thrown()
    {
        Assert.False(_hub.SetDestinationEnabled(Guid.NewGuid(), true).Ok);
        Assert.False(_hub.RemoveDestination(Guid.NewGuid()).Ok);
        Assert.False(_hub.SetRouteEnabled(Guid.NewGuid(), true).Ok);
        Assert.False(_hub.RotateDestinationSecret(Guid.NewGuid()).Ok);
        Assert.Null(_hub.Source(Guid.NewGuid()));
        Assert.Null(_hub.Destination(Guid.NewGuid()));
        Assert.Null(_hub.Delivery(Guid.NewGuid()));
        Assert.Null(_hub.Event(Guid.NewGuid()));
    }

    [Fact]
    public void What_the_hub_returns_is_a_copy_so_callers_cannot_change_its_state()
    {
        var source = _hub.AddSource("S", SignatureScheme.Timestamped).Value!;

        var copy = _hub.Sources().Single();

        Assert.NotSame(source, copy);
        Assert.NotSame(copy, _hub.Sources().Single());
    }
}
