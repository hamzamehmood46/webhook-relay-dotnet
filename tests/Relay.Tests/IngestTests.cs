using Relay.Engine;

namespace Relay.Tests;

public class IngestTests
{
    private readonly Rig _r = new();

    [Fact]
    public void A_genuine_event_is_accepted_stored_and_queued_for_its_destination()
    {
        var result = _r.Post("evt_1", "invoice.paid");

        Assert.Equal(IngestOutcome.Accepted, result.Outcome);
        Assert.Equal(202, result.StatusCode);
        Assert.Equal(1, result.Deliveries);
        var stored = _r.Hub.Event(result.EventId!.Value)!;
        Assert.Equal("evt_1", stored.ProviderEventId);
        Assert.Equal("invoice.paid", stored.Type);
        Assert.Equal(_r.Clock.GetUtcNow(), stored.ReceivedAt);
        Assert.Equal(DeliveryStatus.Pending, _r.Only().Status);
    }

    [Fact]
    public void The_body_is_stored_exactly_as_received()
    {
        var body = "{ \"id\": \"evt_1\",   \"type\":\"a.b\", \"note\": \"spaces  kept\" }";

        var result = _r.PostBody(body);

        Assert.Equal(body, _r.Hub.Event(result.EventId!.Value)!.Body);
    }

    [Fact]
    public void An_unknown_source_is_a_404()
    {
        var result = _r.Hub.Ingest(Guid.NewGuid(), new Dictionary<string, string>(), "{}");

        Assert.Equal(IngestOutcome.UnknownSource, result.Outcome);
        Assert.Equal(404, result.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-secret")]
    [InlineData("tampered")]
    public void A_request_that_is_not_genuine_is_refused_with_401_and_stores_nothing(string kind)
    {
        var body = _r.Body("evt_1");
        var headers = kind switch
        {
            "missing" => new Dictionary<string, string>(),
            "wrong-secret" => _r.Sign(body, secret: "not-the-secret"),
            _ => _r.Sign(body),
        };
        if (kind == "tampered")
        {
            body = body.Replace("4200", "9999");
        }

        var result = _r.Hub.Ingest(_r.Source.Id, headers, body);

        Assert.Equal(IngestOutcome.Unauthorized, result.Outcome);
        Assert.Equal(401, result.StatusCode);
        Assert.Empty(_r.Hub.Events());
        Assert.Empty(_r.Hub.Deliveries());
    }

    [Fact]
    public void A_genuine_signature_on_a_stale_request_is_refused_as_expired()
    {
        var body = _r.Body("evt_1");
        var old = _r.Clock.GetUtcNow().ToUnixTimeSeconds() - 3600;

        var result = _r.Hub.Ingest(_r.Source.Id, _r.Sign(body, timestamp: old), body);

        Assert.Equal(IngestOutcome.Expired, result.Outcome);
        Assert.Equal(401, result.StatusCode);
        Assert.Empty(_r.Hub.Events());
    }

    [Fact]
    public void The_signature_header_name_is_not_case_sensitive()
    {
        var body = _r.Body("evt_1");
        var headers = _r.Sign(body).ToDictionary(p => p.Key.ToUpperInvariant(), p => p.Value);

        Assert.Equal(IngestOutcome.Accepted, _r.Hub.Ingest(_r.Source.Id, headers, body).Outcome);
    }

    [Fact]
    public void A_repeat_of_the_same_provider_event_is_acknowledged_but_not_delivered_again()
    {
        var first = _r.Post("evt_1");
        _r.Run();

        var again = _r.Post("evt_1");

        Assert.Equal(IngestOutcome.Duplicate, again.Outcome);
        Assert.Equal(200, again.StatusCode);
        Assert.Equal(first.EventId, again.EventId);
        Assert.Single(_r.Hub.Events());
        Assert.Single(_r.Hub.Deliveries());
        Assert.Single(_r.Transport.Sent);
    }

    [Fact]
    public void A_repeat_is_recognized_by_its_id_even_if_the_body_differs()
    {
        _r.Post("evt_1", data: new { amount = 1 });

        var again = _r.Post("evt_1", data: new { amount = 2 });

        Assert.Equal(IngestOutcome.Duplicate, again.Outcome);
        Assert.Single(_r.Hub.Events());
    }

    [Fact]
    public void Different_ids_are_different_events()
    {
        _r.Post("evt_1");
        _r.Post("evt_2");

        Assert.Equal(2, _r.Hub.Events().Count);
    }

    [Fact]
    public void The_same_id_from_two_sources_is_two_events()
    {
        var other = _r.Hub.AddSource("Other", SignatureScheme.Timestamped, "whsec_other").Value!;
        _r.Hub.AddRoute(other.Id, _r.Billing.Id);

        _r.Post("evt_1");
        var second = _r.Post("evt_1", source: other);

        Assert.Equal(IngestOutcome.Accepted, second.Outcome);
        Assert.Equal(2, _r.Hub.Events().Count);
    }

    [Fact]
    public void A_forged_repeat_is_refused_as_unauthorized_not_as_a_duplicate()
    {
        _r.Post("evt_1");
        var body = _r.Body("evt_1");

        var result = _r.Hub.Ingest(_r.Source.Id, new Dictionary<string, string>(), body);

        Assert.Equal(IngestOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public void An_id_is_forgotten_after_the_dedupe_window_and_then_accepted_again()
    {
        _r.Post("evt_1");

        _r.Skip(TimeSpan.FromDays(7).Subtract(TimeSpan.FromMinutes(1)));
        Assert.Equal(IngestOutcome.Duplicate, _r.Post("evt_1").Outcome);

        _r.Skip(TimeSpan.FromMinutes(2));
        Assert.Equal(IngestOutcome.Accepted, _r.Post("evt_1").Outcome);
    }

    [Fact]
    public void A_busy_workspace_does_not_forget_ids_that_are_still_inside_the_window()
    {
        _r.Post("evt_1");
        _r.Skip(TimeSpan.FromDays(2));

        _r.Post("evt_2");

        Assert.Equal(IngestOutcome.Duplicate, _r.Post("evt_1").Outcome);
    }

    [Fact]
    public void Without_an_event_id_the_body_itself_identifies_the_event()
    {
        var body = "{\"type\":\"ping\",\"n\":1}";

        var first = _r.PostBody(body);
        var again = _r.PostBody(body);
        var different = _r.PostBody("{\"type\":\"ping\",\"n\":2}");

        Assert.Equal(IngestOutcome.Accepted, first.Outcome);
        Assert.Equal(IngestOutcome.Duplicate, again.Outcome);
        Assert.Equal(IngestOutcome.Accepted, different.Outcome);
        Assert.StartsWith("body:", _r.Hub.Event(first.EventId!.Value)!.ProviderEventId);
    }

    [Fact]
    public void A_missing_event_type_becomes_unknown()
    {
        var result = _r.PostBody("{\"id\":\"evt_1\"}");

        Assert.Equal("unknown", _r.Hub.Event(result.EventId!.Value)!.Type);
    }

    [Fact]
    public void Github_style_sources_take_the_id_and_type_from_headers()
    {
        var github = _r.Hub.AddSource("GitHub", SignatureScheme.HmacBody, "ghsecret", "header:X-GitHub-Delivery", "header:X-GitHub-Event").Value!;
        _r.Hub.AddRoute(github.Id, _r.Billing.Id, ["push"]);
        var body = "{\"ref\":\"refs/heads/main\"}";
        var headers = _r.Sign(body, github);
        headers["X-GitHub-Delivery"] = "72d3162e-cc78";
        headers["X-GitHub-Event"] = "push";

        var result = _r.Hub.Ingest(github.Id, headers, body);
        var again = _r.Hub.Ingest(github.Id, headers, body);

        Assert.Equal(IngestOutcome.Accepted, result.Outcome);
        Assert.Equal(1, result.Deliveries);
        Assert.Equal("72d3162e-cc78", _r.Hub.Event(result.EventId!.Value)!.ProviderEventId);
        Assert.Equal("push", _r.Hub.Event(result.EventId!.Value)!.Type);
        Assert.Equal(IngestOutcome.Duplicate, again.Outcome);
    }

    [Fact]
    public void A_source_that_cannot_sign_accepts_unsigned_posts()
    {
        var open = _r.Hub.AddSource("Legacy", SignatureScheme.None).Value!;

        var result = _r.Hub.Ingest(open.Id, new Dictionary<string, string>(), "{\"id\":\"a\",\"type\":\"x\"}");

        Assert.Equal(IngestOutcome.Accepted, result.Outcome);
        Assert.Equal("", open.Secret);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public void A_body_that_is_not_a_json_object_is_a_400(string body)
    {
        var result = _r.PostBody(body);

        Assert.Equal(IngestOutcome.Malformed, result.Outcome);
        Assert.Equal(400, result.StatusCode);
        Assert.Empty(_r.Hub.Events());
    }

    [Fact]
    public void A_body_over_the_limit_is_a_413_and_one_at_the_limit_is_fine()
    {
        var small = new Rig(new RelayOptions { MaxBodyBytes = 200 });
        var padding = new string('x', 120);
        var fits = "{\"id\":\"a\",\"type\":\"t\",\"p\":\"" + padding + "\"}";
        var exact = fits.Length;
        var tight = new Rig(new RelayOptions { MaxBodyBytes = exact });

        Assert.Equal(IngestOutcome.Accepted, tight.PostBody(fits).Outcome);
        Assert.Equal(IngestOutcome.TooLarge, tight.PostBody(fits + " ").Outcome);
        var big = small.PostBody("{\"id\":\"b\",\"type\":\"t\",\"p\":\"" + new string('x', 400) + "\"}");
        Assert.Equal(413, big.StatusCode);
    }

    [Fact]
    public void An_event_nobody_wants_is_still_stored_and_acknowledged()
    {
        var result = _r.Post("evt_1", "customer.created", source: _r.Source);
        _r.Hub.RemoveRoute(_r.Hub.Routes().Single().Id);
        var second = _r.Post("evt_2");

        Assert.Equal(0, second.Deliveries);
        Assert.Equal(IngestOutcome.Accepted, second.Outcome);
        Assert.Contains("No route", second.Message);
        Assert.NotNull(_r.Hub.Event(second.EventId!.Value));
        Assert.Equal(1, result.Deliveries);
    }
}

public class PauseRateAndRotationTests
{
    private readonly Rig _r = new();

    [Fact]
    public void A_paused_source_answers_503_so_the_provider_retries_and_stores_nothing()
    {
        _r.Hub.PauseSource(_r.Source.Id, true);

        var result = _r.Post("evt_1");

        Assert.Equal(IngestOutcome.Paused, result.Outcome);
        Assert.Equal(503, result.StatusCode);
        Assert.Empty(_r.Hub.Events());
    }

    [Fact]
    public void A_resumed_source_accepts_the_provider_retry_of_the_same_event()
    {
        _r.Hub.PauseSource(_r.Source.Id, true);
        _r.Post("evt_1");
        _r.Hub.PauseSource(_r.Source.Id, false);

        Assert.Equal(IngestOutcome.Accepted, _r.Post("evt_1").Outcome);
    }

    [Fact]
    public void A_pause_does_not_reveal_anything_to_someone_without_a_valid_signature()
    {
        _r.Hub.PauseSource(_r.Source.Id, true);

        var result = _r.Hub.Ingest(_r.Source.Id, new Dictionary<string, string>(), _r.Body());

        Assert.Equal(IngestOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public void Pausing_an_unknown_source_is_refused() => Assert.False(_r.Hub.PauseSource(Guid.NewGuid(), true).Ok);

    [Fact]
    public void Past_the_rate_limit_events_get_429_and_the_window_then_slides()
    {
        var slow = _r.Hub.AddSource("Slow", SignatureScheme.Timestamped, "s", ratePerMinute: 3).Value!;

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(IngestOutcome.Accepted, _r.Post("a" + i, source: slow).Outcome);
        }

        var limited = _r.Post("a3", source: slow);
        Assert.Equal(IngestOutcome.RateLimited, limited.Outcome);
        Assert.Equal(429, limited.StatusCode);

        _r.Skip(TimeSpan.FromSeconds(59));
        Assert.Equal(IngestOutcome.RateLimited, _r.Post("a3", source: slow).Outcome);

        _r.Skip(TimeSpan.FromSeconds(1));
        Assert.Equal(IngestOutcome.Accepted, _r.Post("a3", source: slow).Outcome);
    }

    [Fact]
    public void The_limit_is_per_source()
    {
        var a = _r.Hub.AddSource("A", SignatureScheme.Timestamped, "a", ratePerMinute: 1).Value!;
        var b = _r.Hub.AddSource("B", SignatureScheme.Timestamped, "b", ratePerMinute: 1).Value!;

        _r.Post("1", source: a);

        Assert.Equal(IngestOutcome.RateLimited, _r.Post("2", source: a).Outcome);
        Assert.Equal(IngestOutcome.Accepted, _r.Post("1", source: b).Outcome);
    }

    [Fact]
    public void Requests_that_fail_verification_do_not_use_up_the_limit()
    {
        var tight = _r.Hub.AddSource("Tight", SignatureScheme.Timestamped, "s", ratePerMinute: 2).Value!;
        for (var i = 0; i < 10; i++)
        {
            _r.Hub.Ingest(tight.Id, new Dictionary<string, string>(), _r.Body("junk" + i));
        }

        Assert.Equal(IngestOutcome.Accepted, _r.Post("real1", source: tight).Outcome);
        Assert.Equal(IngestOutcome.Accepted, _r.Post("real2", source: tight).Outcome);
    }

    [Fact]
    public void After_a_rotation_the_new_secret_works_and_the_old_one_works_for_the_grace_period_only()
    {
        _r.Hub.RotateSourceSecret(_r.Source.Id, "whsec_new", TimeSpan.FromHours(1));

        Assert.Equal(IngestOutcome.Accepted, _r.Post("e1", secret: "whsec_new").Outcome);
        Assert.Equal(IngestOutcome.Accepted, _r.Post("e2", secret: "whsec_test").Outcome);

        _r.Skip(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(IngestOutcome.Unauthorized, _r.Post("e3", secret: "whsec_test").Outcome);
        Assert.Equal(IngestOutcome.Accepted, _r.Post("e4", secret: "whsec_new").Outcome);
    }

    [Fact]
    public void Rotation_without_a_given_secret_makes_a_new_one()
    {
        var rotated = _r.Hub.RotateSourceSecret(_r.Source.Id, null, TimeSpan.FromMinutes(5)).Value!;

        Assert.NotEqual("whsec_test", rotated.Secret);
        Assert.StartsWith("whsec_", rotated.Secret);
    }

    [Fact]
    public void A_source_without_a_secret_or_an_unknown_source_cannot_be_rotated()
    {
        var open = _r.Hub.AddSource("Open", SignatureScheme.None).Value!;

        Assert.False(_r.Hub.RotateSourceSecret(open.Id, null, TimeSpan.FromHours(1)).Ok);
        Assert.False(_r.Hub.RotateSourceSecret(Guid.NewGuid(), null, TimeSpan.FromHours(1)).Ok);
    }
}

public class IngestConcurrencyTests
{
    [Fact]
    public async Task Fifty_parallel_posts_of_the_same_event_are_accepted_once_and_the_rest_are_duplicates()
    {
        var r = new Rig();
        var body = r.Body("evt_same");
        var headers = r.Sign(body);

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => r.Hub.Ingest(r.Source.Id, headers, body))));

        Assert.Equal(1, results.Count(x => x.Outcome == IngestOutcome.Accepted));
        Assert.Equal(49, results.Count(x => x.Outcome == IngestOutcome.Duplicate));
        Assert.Single(r.Hub.Events());
        Assert.Single(r.Hub.Deliveries());
    }

    [Fact]
    public async Task Two_hundred_parallel_different_events_are_all_stored_with_one_delivery_each()
    {
        var r = new Rig();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => r.Post("evt_" + i))));

        Assert.Equal(200, r.Hub.Events(limit: 500).Count);
        Assert.Equal(200, r.Hub.Deliveries(limit: 1000).Count);
    }
}
