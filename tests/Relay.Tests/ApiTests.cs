using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Relay.Tests;

public class ApiTests : IClassFixture<ApiTests.App>
{
    public sealed class App : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Worker:Enabled", "false");
    }

    private readonly App _app;

    public ApiTests(App app) => _app = app;

    private sealed class Workspace(HttpClient client, string key)
    {
        public HttpClient Client { get; } = client;
        public string Key { get; } = key;
        public HttpRequestMessage Request(HttpMethod method, string url, object? body = null)
        {
            var message = new HttpRequestMessage(method, url);
            message.Headers.Add("X-Api-Key", Key);
            if (body is not null)
            {
                message.Content = JsonContent.Create(body);
            }

            return message;
        }

        public async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string url, object? body = null)
        {
            var response = await Client.SendAsync(Request(method, url, body));
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
        }

        public Task<(HttpStatusCode Status, JsonElement Body)> Get(string url) => Send(HttpMethod.Get, url);
        public Task<(HttpStatusCode Status, JsonElement Body)> Post(string url, object? body = null) => Send(HttpMethod.Post, url, body ?? new { });
    }

    private async Task<Workspace> NewWorkspace(string name = "Acme")
    {
        var client = _app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/tenants", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Workspace(client, body.GetProperty("apiKey").GetString()!);
    }

    private static async Task<Guid> Id(Task<(HttpStatusCode Status, JsonElement Body)> call)
    {
        var (status, body) = await call;
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<(Guid Source, Guid Destination)> Wire(Workspace w, string behavior = "healthy", string scheme = "Timestamped")
    {
        var source = await Id(w.Post("/api/sources", new { name = "Payments", scheme }));
        var destination = await Id(w.Post("/api/destinations", new { name = "Billing", url = "demo://" + behavior }));
        await Id(w.Post("/api/routes", new { sourceId = source, destinationId = destination }));
        return (source, destination);
    }

    private static async Task<JsonElement> Sample(Workspace w, Guid source, string? eventId = null, string? type = null)
    {
        var (status, body) = await w.Post($"/api/sources/{source}/sample", new { eventId, type });
        Assert.Equal(HttpStatusCode.OK, status);
        return body;
    }

    private static async Task<HttpResponseMessage> Deliver(HttpClient client, Guid source, JsonElement sample, Action<HttpRequestMessage>? tweak = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"/in/{source}") { Content = new StringContent(sample.GetProperty("body").GetString()!, Encoding.UTF8, "application/json") };
        foreach (var header in sample.GetProperty("headers").EnumerateObject())
        {
            message.Headers.Add(header.Name, header.Value.GetString());
        }

        tweak?.Invoke(message);
        return await client.SendAsync(message);
    }

    [Fact]
    public async Task The_service_opens_on_the_swagger_page()
    {
        var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/swagger", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_workspace_needs_a_name(string name)
    {
        var response = await _app.CreateClient().PostAsJsonAsync("/api/tenants", new { name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Everything_except_creating_a_workspace_needs_an_api_key()
    {
        var client = _app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/sources")).StatusCode);
        var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/sources");
        wrong.Headers.Add("X-Api-Key", "rk_wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);
    }

    [Fact]
    public async Task A_workspace_cannot_see_or_touch_another_workspaces_data()
    {
        var a = await NewWorkspace("A");
        var b = await NewWorkspace("B");
        var (source, destination) = await Wire(a);
        var sample = await Sample(a, source);
        var delivered = await Deliver(a.Client, source, sample);
        var eventId = (await delivered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();

        Assert.Equal(0, (await b.Get("/api/sources")).Body.GetArrayLength());
        Assert.Equal(0, (await b.Get("/api/destinations")).Body.GetArrayLength());
        Assert.Equal(0, (await b.Get("/api/events")).Body.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await b.Get($"/api/events/{eventId}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Post($"/api/sources/{source}/sample")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Post($"/api/events/{eventId}/replay")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Send(HttpMethod.Delete, $"/api/destinations/{destination}")).Status);
        Assert.Equal(1, (await a.Get("/api/events")).Body.GetArrayLength());
    }

    [Fact]
    public async Task A_route_cannot_connect_to_another_workspaces_source_or_destination()
    {
        var a = await NewWorkspace("A");
        var b = await NewWorkspace("B");
        var (source, destination) = await Wire(a);
        var ownDestination = await Id(b.Post("/api/destinations", new { name = "Mine", url = "demo://healthy" }));
        var ownSource = await Id(b.Post("/api/sources", new { name = "Mine" }));

        var (s1, _) = await b.Post("/api/routes", new { sourceId = source, destinationId = ownDestination });
        var (s2, _) = await b.Post("/api/routes", new { sourceId = ownSource, destinationId = destination });

        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.Equal(HttpStatusCode.BadRequest, s2);
    }

    [Fact]
    public async Task A_signed_webhook_is_accepted_delivered_and_signed_for_the_destination()
    {
        var w = await NewWorkspace();
        var (source, destination) = await Wire(w);
        var sample = await Sample(w, source, "evt_abc", "invoice.paid");

        var response = await Deliver(w.Client, source, sample);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Accepted", accepted.GetProperty("outcome").GetString());
        Assert.Equal(1, accepted.GetProperty("deliveries").GetInt32());
        Assert.Equal(1, (await w.Post("/api/worker/run")).Body.GetProperty("attempted").GetInt32());

        var received = (await w.Get($"/api/destinations/{destination}/received")).Body;
        Assert.Equal(1, received.GetArrayLength());
        Assert.True(received[0].GetProperty("signatureValid").GetBoolean());
        Assert.Equal("invoice.paid", received[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Posting_the_same_event_twice_delivers_it_once()
    {
        var w = await NewWorkspace();
        var (source, destination) = await Wire(w);
        var sample = await Sample(w, source, "evt_dup");

        var first = await Deliver(w.Client, source, sample);
        var second = await Deliver(w.Client, source, sample);
        await w.Post("/api/worker/run");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("Duplicate", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString());
        Assert.Equal(1, (await w.Get($"/api/destinations/{destination}/received")).Body.GetArrayLength());
        Assert.Equal(1, (await w.Get("/api/events")).Body.GetArrayLength());
    }

    [Fact]
    public async Task A_request_without_a_valid_signature_is_refused_with_401()
    {
        var w = await NewWorkspace();
        var (source, _) = await Wire(w);
        var sample = await Sample(w, source);

        var tampered = new HttpRequestMessage(HttpMethod.Post, $"/in/{source}") { Content = new StringContent(sample.GetProperty("body").GetString()!.Replace("4200", "1"), Encoding.UTF8, "application/json") };
        foreach (var header in sample.GetProperty("headers").EnumerateObject())
        {
            tampered.Headers.Add(header.Name, header.Value.GetString());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await w.Client.SendAsync(tampered)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.Client.PostAsync($"/in/{source}", new StringContent(sample.GetProperty("body").GetString()!, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(0, (await w.Get("/api/events")).Body.GetArrayLength());
    }

    [Fact]
    public async Task A_github_style_source_signs_the_body_only()
    {
        var w = await NewWorkspace();
        var (source, _) = await Wire(w, scheme: "HmacBody");
        var sample = await Sample(w, source);

        var response = await Deliver(w.Client, source, sample);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(sample.GetProperty("headers").TryGetProperty("X-Hub-Signature-256", out _));
    }

    [Fact]
    public async Task A_url_for_a_source_that_does_not_exist_is_a_404()
    {
        var response = await _app.CreateClient().PostAsync($"/in/{Guid.NewGuid()}", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_paused_source_answers_503()
    {
        var w = await NewWorkspace();
        var (source, _) = await Wire(w);
        var sample = await Sample(w, source);
        await w.Post($"/api/sources/{source}/pause", new { paused = true });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Deliver(w.Client, source, sample)).StatusCode);
    }

    [Fact]
    public async Task Skipping_the_clock_makes_a_signed_sample_expire_so_replay_attacks_fail()
    {
        var w = await NewWorkspace();
        var (source, _) = await Wire(w);
        var sample = await Sample(w, source);
        await w.Post("/api/demo/skip", new { seconds = 3600 });

        var response = await Deliver(w.Client, source, sample);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Expired", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_flaky_destination_recovers_after_retries_with_the_clock_skipped()
    {
        var w = await NewWorkspace();
        var (source, destination) = await Wire(w, "flaky");
        await Deliver(w.Client, source, await Sample(w, source, "evt_flaky"));

        await w.Post("/api/worker/run");
        await w.Post("/api/demo/skip", new { seconds = 120 });
        await w.Post("/api/worker/run");
        await w.Post("/api/demo/skip", new { seconds = 600 });
        await w.Post("/api/worker/run");

        var deliveries = (await w.Get("/api/deliveries")).Body;
        Assert.Equal("Succeeded", deliveries[0].GetProperty("status").GetString());
        Assert.Equal(3, deliveries[0].GetProperty("attempts").GetInt32());
        Assert.Equal(1, (await w.Get($"/api/destinations/{destination}/received")).Body.GetArrayLength());
    }

    [Fact]
    public async Task A_destination_that_rejects_dead_letters_and_a_replay_can_be_queued()
    {
        var w = await NewWorkspace();
        var (source, destination) = await Wire(w, "rejects");
        await Deliver(w.Client, source, await Sample(w, source));
        await w.Post("/api/worker/run");

        var dead = (await w.Get("/api/deliveries?status=DeadLettered")).Body;
        Assert.Equal(1, dead.GetArrayLength());
        var id = dead[0].GetProperty("id").GetGuid();

        var (status, replay) = await w.Post($"/api/deliveries/{id}/replay");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("Pending", replay.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await w.Post($"/api/deliveries/{replay.GetProperty("id").GetGuid()}/replay")).Status);
        Assert.Equal(0, (await w.Post($"/api/destinations/{destination}/replay-dead-letters")).Body.GetProperty("queued").GetInt32());
    }

    [Fact]
    public async Task A_lost_acknowledgement_delivers_twice_so_receivers_must_dedupe_on_the_event_id()
    {
        var w = await NewWorkspace();
        var (source, destination) = await Wire(w, "lost-ack");
        await Deliver(w.Client, source, await Sample(w, source));

        await w.Post("/api/worker/run");
        await w.Post("/api/demo/skip", new { seconds = 90 });
        await w.Post("/api/worker/run");

        var received = (await w.Get($"/api/destinations/{destination}/received")).Body;
        Assert.Equal(2, received.GetArrayLength());
        Assert.Equal(received[0].GetProperty("eventId").GetGuid(), received[1].GetProperty("eventId").GetGuid());
    }

    [Fact]
    public async Task A_gone_destination_is_switched_off()
    {
        var w = await NewWorkspace();
        var (source, _) = await Wire(w, "gone");
        await Deliver(w.Client, source, await Sample(w, source));

        await w.Post("/api/worker/run");

        var destination = (await w.Get("/api/destinations")).Body[0];
        Assert.False(destination.GetProperty("enabled").GetBoolean());
        Assert.Contains("410", destination.GetProperty("disabledReason").GetString());
    }

    [Fact]
    public async Task Only_simulated_destinations_are_allowed_so_the_demo_cannot_be_used_to_call_other_sites()
    {
        var w = await NewWorkspace();

        var (status, body) = await w.Post("/api/destinations", new { name = "Evil", url = "https://example.com/hook" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("simulated", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Secrets_are_shown_once_and_masked_afterwards()
    {
        var w = await NewWorkspace();
        var (_, created) = await w.Post("/api/sources", new { name = "Payments" });

        var full = created.GetProperty("secret").GetString()!;
        var listed = (await w.Get("/api/sources")).Body[0].GetProperty("secret").GetString()!;

        Assert.StartsWith("whsec_", full);
        Assert.NotEqual(full, listed);
        Assert.EndsWith("…", listed);
    }

    [Fact]
    public async Task Bad_input_is_a_400_with_a_reason()
    {
        var w = await NewWorkspace();

        var (s1, b1) = await w.Post("/api/sources", new { name = "" });
        var (s2, _) = await w.Post("/api/routes", new { sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.Contains("name", b1.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, s2);
    }

    [Fact]
    public async Task The_overview_counts_what_has_happened()
    {
        var w = await NewWorkspace();
        var (source, _) = await Wire(w);
        await Deliver(w.Client, source, await Sample(w, source));
        await w.Post("/api/worker/run");

        var overview = (await w.Get("/api/overview")).Body;

        Assert.Equal(1, overview.GetProperty("sources").GetInt32());
        Assert.Equal(1, overview.GetProperty("events").GetInt32());
        Assert.Equal(1, overview.GetProperty("deliveries").GetProperty("Succeeded").GetInt32());
        Assert.Equal("Closed", overview.GetProperty("destinationHealth")[0].GetProperty("circuit").GetString());
    }

    [Fact]
    public async Task An_event_can_be_replayed_to_a_chosen_destination()
    {
        var w = await NewWorkspace();
        var (source, _) = await Wire(w);
        var extra = await Id(w.Post("/api/destinations", new { name = "Audit", url = "demo://healthy" }));
        var response = await Deliver(w.Client, source, await Sample(w, source));
        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();

        var (status, body) = await w.Post($"/api/events/{eventId}/replay", new { destinationId = extra });

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(extra, body.GetProperty("deliveries")[0].GetProperty("destinationId").GetGuid());
    }

    [Fact]
    public async Task Simulate_signs_and_receives_in_one_step_and_a_repeat_is_a_duplicate()
    {
        var w = await NewWorkspace();
        var (source, _) = await Wire(w);

        var (_, first) = await w.Post($"/api/sources/{source}/simulate", new { eventId = "evt_sim" });
        var (_, again) = await w.Post($"/api/sources/{source}/simulate", new { eventId = "evt_sim" });

        Assert.Equal("Accepted", first.GetProperty("outcome").GetString());
        Assert.Equal(202, first.GetProperty("httpStatus").GetInt32());
        Assert.Equal("Duplicate", again.GetProperty("outcome").GetString());
        Assert.Equal(200, again.GetProperty("httpStatus").GetInt32());
    }

    [Fact]
    public async Task Simulate_can_tamper_with_the_body_or_age_the_signature_to_show_the_refusals()
    {
        var w = await NewWorkspace();
        var (source, _) = await Wire(w);

        var (_, tampered) = await w.Post($"/api/sources/{source}/simulate", new { tamper = true });
        var (_, stale) = await w.Post($"/api/sources/{source}/simulate", new { ageSeconds = 3600 });

        Assert.Equal("Unauthorized", tampered.GetProperty("outcome").GetString());
        Assert.Equal(401, tampered.GetProperty("httpStatus").GetInt32());
        Assert.Equal("Expired", stale.GetProperty("outcome").GetString());
        Assert.Equal(0, (await w.Get("/api/events")).Body.GetArrayLength());
    }

    [Fact]
    public async Task Simulate_on_an_unknown_source_is_a_404()
    {
        var w = await NewWorkspace();

        Assert.Equal(HttpStatusCode.NotFound, (await w.Post($"/api/sources/{Guid.NewGuid()}/simulate")).Status);
    }

    [Fact]
    public async Task The_sample_headers_and_body_belong_together()
    {
        var w = await NewWorkspace();
        var (source, _) = await Wire(w);

        var sample = await Sample(w, source, "evt_pair");

        Assert.Contains("evt_pair", sample.GetProperty("body").GetString());
        Assert.StartsWith("t=", sample.GetProperty("headers").GetProperty("Stripe-Signature").GetString());
    }

    [Fact]
    public async Task The_tenant_registry_retires_the_oldest_workspace_when_full()
    {
        var registry = new Relay.Api.TenantRegistry();
        var first = registry.Create("first");
        for (var i = 0; i < Relay.Api.TenantRegistry.MaxTenants; i++)
        {
            registry.Create("t" + i);
        }

        Assert.Null(registry.ByApiKey(first.ApiKey));
        Assert.Equal(Relay.Api.TenantRegistry.MaxTenants, registry.All().Count);
        Assert.Null(registry.ByApiKey(null));
        Assert.Null(registry.ByApiKey(""));
    }
}
