using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;
using Relay.Api;
using Relay.Engine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TenantRegistry>();
if (builder.Configuration.GetValue("Worker:Enabled", true))
{
    builder.Services.AddHostedService<DeliveryWorker>();
}

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Webhook Relay",
        Version = "v1",
        Description = "Receive webhooks from providers, verify them, drop repeats, route each event to the right destinations and deliver with retries, dead letters, replay and a circuit breaker.\n\n" +
                      "1) POST /api/tenants to get an API key, then press Authorize and paste it. 2) Create a source, a destination and a route. 3) POST /api/sources/{id}/sample to get a correctly signed request, send it to /in/{sourceId}. " +
                      "Destination urls are demo://healthy, flaky, slow-down, lost-ack, down, gone or rejects.",
    });
    o.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Api-Key", Description = "The key returned by POST /api/tenants." });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }] = [] });
});

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// ---- Public: create a workspace, and receive webhooks from providers ----
app.MapPost("/api/tenants", (NewTenant body, TenantRegistry tenants) =>
{
    if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 60)
    {
        return Results.BadRequest(new { error = "A workspace needs a name of up to 60 characters." });
    }

    var tenant = tenants.Create(body.Name);
    return Results.Created("/api/me", new { id = tenant.Id, tenant.Name, apiKey = tenant.ApiKey, note = "Send the key as the X-Api-Key header. The demo keeps up to 100 workspaces and retires the oldest." });
});

// Providers post here. There is no API key: the signature is the proof.
app.MapPost("/in/{sourceId:guid}", async (Guid sourceId, HttpRequest request, TenantRegistry tenants) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());
    var tenant = tenants.BySource(sourceId);
    var result = tenant is null ? new IngestResult(IngestOutcome.UnknownSource, "No such source.") : tenant.Hub.Ingest(sourceId, headers, body);
    return Results.Json(new { outcome = result.Outcome, result.Message, result.EventId, result.Deliveries }, statusCode: result.StatusCode);
}).WithTags("Receive").ExcludeFromDescription();

// ---- Authenticated: everything a customer manages ----
var api = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
{
    var tenants = ctx.HttpContext.RequestServices.GetRequiredService<TenantRegistry>();
    var tenant = tenants.ByApiKey(ctx.HttpContext.Request.Headers["X-Api-Key"].ToString());
    if (tenant is null)
    {
        return Results.Json(new { error = "Send a valid X-Api-Key header. Get one from POST /api/tenants." }, statusCode: 401);
    }

    ctx.HttpContext.Items["tenant"] = tenant;
    return await next(ctx);
});

api.MapGet("/me", (HttpContext c) => new { c.Tenant().Id, c.Tenant().Name, now = c.Tenant().Clock.GetUtcNow() });

api.MapGet("/overview", (HttpContext c) =>
{
    var hub = c.Tenant().Hub;
    var deliveries = hub.Deliveries(limit: 1000);
    return new
    {
        sources = hub.Sources().Count,
        destinations = hub.Destinations().Count,
        routes = hub.Routes().Count,
        events = hub.Events(limit: 500).Count,
        deliveries = Enum.GetValues<DeliveryStatus>().ToDictionary(s => s.ToString(), s => deliveries.Count(d => d.Status == s)),
        destinationHealth = hub.Health(),
        now = c.Tenant().Clock.GetUtcNow(),
    };
});

// Sources
api.MapPost("/sources", (HttpContext c, NewSource body) =>
{
    var created = c.Tenant().Hub.AddSource(body.Name ?? "", body.Scheme ?? SignatureScheme.Timestamped, body.Secret, body.EventIdPath ?? "id", body.EventTypePath ?? "type", body.RatePerMinute ?? 600);
    return created.Ok
        ? Results.Created($"/api/sources/{created.Value!.Id}", SourceView(created.Value, true))
        : Results.BadRequest(new { error = created.Message });
}).WithTags("Sources");

api.MapGet("/sources", (HttpContext c) => c.Tenant().Hub.Sources().Select(s => SourceView(s, false))).WithTags("Sources");

api.MapPost("/sources/{id:guid}/pause", (Guid id, PauseRequest body, HttpContext c) =>
    Outcome(c.Tenant().Hub.PauseSource(id, body.Paused))).WithTags("Sources");

api.MapPost("/sources/{id:guid}/rotate-secret", (Guid id, RotateRequest? body, HttpContext c) =>
{
    var rotated = c.Tenant().Hub.RotateSourceSecret(id, body?.Secret, TimeSpan.FromMinutes(body?.GraceMinutes ?? 60));
    return rotated.Ok ? Results.Ok(SourceView(rotated.Value!, true)) : Results.BadRequest(new { error = rotated.Message });
}).WithTags("Sources");

// Helpers for trying things out without a real provider.
api.MapPost("/sources/{id:guid}/sample", (Guid id, SampleRequest? body, HttpContext c) =>
{
    var tenant = c.Tenant();
    if (tenant.Hub.Source(id) is not { } source)
    {
        return Results.NotFound();
    }

    var (headers, text) = Build(tenant, source, body, body?.AgeSeconds ?? 0);
    return Results.Ok(new { postTo = $"/in/{id}", headers, body = text, note = "Post this body with these headers to the ingest url. Posting it twice shows de-duplication." });
}).WithTags("Sources");

// Signs a request the way the provider would and receives it, in one step, so it can be tried from this page.
api.MapPost("/sources/{id:guid}/simulate", (Guid id, SampleRequest? body, HttpContext c) =>
{
    var tenant = c.Tenant();
    if (tenant.Hub.Source(id) is not { } source)
    {
        return Results.NotFound();
    }

    var (headers, text) = Build(tenant, source, body, body?.AgeSeconds ?? 0);
    if (body?.Tamper == true)
    {
        text = text.Replace("4200", "9999");
    }

    var result = tenant.Hub.Ingest(id, headers, text);
    return Results.Json(new { outcome = result.Outcome, result.Message, result.EventId, result.Deliveries, httpStatus = result.StatusCode }, statusCode: 200);
}).WithTags("Sources");
// Destinations
api.MapPost("/destinations", (HttpContext c, NewDestination body) =>
{
    if (DemoTransport.BehaviorOf(body.Url ?? "") is null)
    {
        return Results.BadRequest(new { error = $"This demo only sends to simulated receivers: {string.Join(", ", DemoTransport.Behaviors.Select(b => "demo://" + b))}." });
    }

    var created = c.Tenant().Hub.AddDestination(body.Name ?? "", body.Url!);
    return created.Ok
        ? Results.Created($"/api/destinations/{created.Value!.Id}", DestinationView(created.Value, true))
        : Results.BadRequest(new { error = created.Message });
}).WithTags("Destinations");

api.MapGet("/destinations", (HttpContext c) => c.Tenant().Hub.Destinations().Select(d => DestinationView(d, false))).WithTags("Destinations");

api.MapPost("/destinations/{id:guid}/enable", (Guid id, EnableRequest body, HttpContext c) =>
    Outcome(c.Tenant().Hub.SetDestinationEnabled(id, body.Enabled))).WithTags("Destinations");

api.MapDelete("/destinations/{id:guid}", (Guid id, HttpContext c) => Outcome(c.Tenant().Hub.RemoveDestination(id))).WithTags("Destinations");

api.MapPost("/destinations/{id:guid}/rotate-secret", (Guid id, HttpContext c) =>
{
    var rotated = c.Tenant().Hub.RotateDestinationSecret(id);
    return rotated.Ok ? Results.Ok(DestinationView(rotated.Value!, true)) : Results.NotFound();
}).WithTags("Destinations");

api.MapPost("/destinations/{id:guid}/replay-dead-letters", (Guid id, HttpContext c) =>
    c.Tenant().Hub.Destination(id) is null ? Results.NotFound() : Results.Accepted(value: new { queued = c.Tenant().Hub.ReplayDeadLetters(id) })).WithTags("Destinations");

api.MapGet("/destinations/{id:guid}/received", (Guid id, HttpContext c) =>
    c.Tenant().Hub.Destination(id) is null ? Results.NotFound() : Results.Ok(c.Tenant().Transport.Inbox(id).OrderByDescending(r => r.At))).WithTags("Destinations");

// Routes
api.MapPost("/routes", (HttpContext c, NewRoute body) =>
{
    var where = body.Where is null ? null : new PayloadCondition(body.Where.Path ?? "", body.Where.Value ?? "");
    var created = c.Tenant().Hub.AddRoute(body.SourceId, body.DestinationId, body.EventTypes, where);
    return created.Ok ? Results.Created($"/api/routes/{created.Value!.Id}", created.Value) : Results.BadRequest(new { error = created.Message });
}).WithTags("Routes");

api.MapGet("/routes", (HttpContext c) => c.Tenant().Hub.Routes()).WithTags("Routes");
api.MapPost("/routes/{id:guid}/enable", (Guid id, EnableRequest body, HttpContext c) => Outcome(c.Tenant().Hub.SetRouteEnabled(id, body.Enabled))).WithTags("Routes");
api.MapDelete("/routes/{id:guid}", (Guid id, HttpContext c) => Outcome(c.Tenant().Hub.RemoveRoute(id))).WithTags("Routes");

// Events and deliveries
api.MapGet("/events", (HttpContext c, Guid? sourceId, int? limit) => c.Tenant().Hub.Events(sourceId, limit ?? 100)).WithTags("Events");

api.MapGet("/events/{id:guid}", (Guid id, HttpContext c) =>
    c.Tenant().Hub.Event(id) is { } e ? Results.Ok(new { evt = e, deliveries = c.Tenant().Hub.Deliveries(eventId: id) }) : Results.NotFound()).WithTags("Events");

api.MapPost("/events/{id:guid}/replay", (Guid id, ReplayEventRequest? body, HttpContext c) =>
{
    var result = c.Tenant().Hub.ReplayEvent(id, body?.DestinationId);
    return result.Ok ? Results.Accepted(value: new { result.Message, deliveries = result.Value }) : Results.NotFound(new { error = result.Message });
}).WithTags("Events");

api.MapGet("/deliveries", (HttpContext c, DeliveryStatus? status, Guid? destinationId, Guid? eventId, int? limit) =>
    c.Tenant().Hub.Deliveries(status, destinationId, eventId, limit ?? 200)).WithTags("Deliveries");

api.MapGet("/deliveries/{id:guid}", (Guid id, HttpContext c) => c.Tenant().Hub.Delivery(id) is { } d ? Results.Ok(d) : Results.NotFound()).WithTags("Deliveries");

api.MapPost("/deliveries/{id:guid}/replay", (Guid id, HttpContext c) =>
{
    var result = c.Tenant().Hub.ReplayDelivery(id);
    return result.Ok ? Results.Accepted(value: result.Value) : Results.Conflict(new { error = result.Message });
}).WithTags("Deliveries");

// Demo helpers
api.MapPost("/worker/run", async (HttpContext c) => new { attempted = await c.Tenant().Hub.ProcessDueAsync(c.Tenant().Transport) }).WithTags("Demo");

api.MapPost("/demo/skip", (SkipRequest body, HttpContext c) =>
{
    var seconds = Math.Clamp(body.Seconds, 0, 7 * 24 * 3600);
    return Results.Ok(new { now = c.Tenant().Clock.Skip(TimeSpan.FromSeconds(seconds)) });
}).WithTags("Demo");

app.Run();

static (Dictionary<string, string> Headers, string Body) Build(Tenant tenant, Source source, SampleRequest? request, int ageSeconds)
{
    var eventId = string.IsNullOrWhiteSpace(request?.EventId) ? "evt_" + Guid.NewGuid().ToString("N")[..12] : request.EventId;
    var type = string.IsNullOrWhiteSpace(request?.Type) ? "invoice.paid" : request.Type;
    var text = request?.Payload is { } payload ? payload.GetRawText() : JsonSerializer.Serialize(new { id = eventId, type, data = new { amount = 4200, currency = "usd" } });
    var headers = new Dictionary<string, string>();
    var (name, value) = Signing.SignInbound(source.Scheme, source.Secret, tenant.Clock.GetUtcNow().ToUnixTimeSeconds() - ageSeconds, text);
    if (name != "")
    {
        headers[name] = value;
    }

    return (headers, text);
}

static object SourceView(Source s, bool showSecret) => new
{
    s.Id,
    s.Name,
    s.Scheme,
    secret = showSecret ? s.Secret : Mask(s.Secret),
    s.EventIdPath,
    s.EventTypePath,
    s.RatePerMinute,
    s.Paused,
    ingestUrl = $"/in/{s.Id}",
};

static object DestinationView(Destination d, bool showSecret) => new
{
    d.Id,
    d.Name,
    d.Url,
    secret = showSecret ? d.Secret : Mask(d.Secret),
    d.Enabled,
    d.DisabledReason,
    d.ConsecutiveFailures,
    d.CircuitOpenUntil,
};

static string Mask(string secret) => secret.Length <= 10 ? secret : secret[..10] + "…";

static IResult Outcome(Result result) => result.Ok ? Results.Ok(new { result.Message }) : Results.NotFound(new { error = result.Message });

public static class TenantExtensions
{
    public static Tenant Tenant(this HttpContext context) => (Tenant)context.Items["tenant"]!;
}

public record NewTenant(string? Name);
public record NewSource(string? Name, SignatureScheme? Scheme, string? Secret, string? EventIdPath, string? EventTypePath, int? RatePerMinute);
public record PauseRequest(bool Paused);
public record RotateRequest(string? Secret, int? GraceMinutes);
public record SampleRequest(string? EventId, string? Type, JsonElement? Payload, int? AgeSeconds, bool? Tamper);
public record NewDestination(string? Name, string? Url);
public record EnableRequest(bool Enabled);
public record NewRoute(Guid SourceId, Guid DestinationId, string[]? EventTypes, WhereClause? Where);
public record WhereClause(string? Path, string? Value);
public record ReplayEventRequest(Guid? DestinationId);
public record SkipRequest(int Seconds);

public partial class Program;
