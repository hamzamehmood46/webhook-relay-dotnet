# Webhook Relay: receive, verify, route and deliver webhooks

[![CI](https://github.com/hamzamehmood46/webhook-relay-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/hamzamehmood46/webhook-relay-dotnet/actions/workflows/ci.yml)

**Live demo: [webhook-relay-dotnet.onrender.com](https://webhook-relay-dotnet.onrender.com/swagger)** (Swagger; the free host sleeps when idle, so the first request can take about a minute)

A webhook relay in C#, the kind of service you put between payment, source-control or CRM providers and your own systems. A provider posts an event to a source url. The relay proves it is genuine, drops repeats, routes it to every destination that wants it and delivers it with retries, dead letters, replay and a circuit breaker for destinations that are down. Each customer gets a private workspace behind an API key.

## Try it (3 minutes, all from the Swagger page)

1. `POST /api/tenants` with a name. Copy the `apiKey`, press **Authorize** and paste it.
2. `POST /api/sources` (defaults are fine), `POST /api/destinations` with `{"name":"Billing","url":"demo://flaky"}`, then `POST /api/routes` with the two ids and `"eventTypes":["invoice.*"]`.
3. `POST /api/sources/{id}/simulate`: the relay signs a request the way the provider would and receives it. Run it twice with the same `eventId` to see **Duplicate**. Send `{"tamper":true}` to see a refusal, and `{"ageSeconds":3600}` to see an expired signature refused.
4. `POST /api/worker/run`, then `POST /api/demo/skip` with `{"seconds":600}`, and repeat. `GET /api/deliveries` shows the flaky destination failing twice with 503 and then succeeding, with every attempt kept.
5. Try the other behaviors: `demo://slow-down` (429 with Retry-After), `demo://lost-ack` (the receiver processed it but the reply was lost, so it arrives twice), `demo://down` (watch the circuit breaker open), `demo://gone` (410 switches the destination off), `demo://rejects` (400, dead-lettered at once, then replay it).

The demo never calls other people's servers: destinations are simulated receivers, so it cannot be used to forward requests.

## What it does

| Capability | Behavior |
|---|---|
| **Signature verification** | Stripe style (`t=...,v1=...` over "{t}.{body}", with a five minute tolerance against replays) and GitHub style (`sha256=...` over the body), with constant-time comparison. A missing, malformed, mismatched or stale signature is refused with 401 and stores nothing. |
| **Secret rotation** | A rotated secret keeps the old one valid for a grace period, so no event is rejected while the provider catches up. |
| **De-duplication** | Providers retry, so the same event arrives more than once. Repeats are recognized by the provider's own event id (read from a JSON path or a header, for example `header:X-GitHub-Delivery`), or by a hash of the body if there is none, and are acknowledged without being delivered twice. A forged repeat is refused as unauthorized, not acknowledged. |
| **Routing** | Event types by exact name, prefix (`invoice.*`) or everything, plus an optional payload condition such as `data.currency = usd`. One event fans out to every matching destination, and a destination reached by two routes still gets it once. |
| **Delivery** | Every delivery is signed afresh for the destination (id and timestamp included, so a body cannot be replayed under another id). Backoff of 1 minute, 5, 30, 2 hours and 6 hours, six attempts, honoring `Retry-After` (never shortening the schedule, capped at a day). |
| **Failure handling** | 5xx, 408, 429 and timeouts are retried. Other 4xx are final, because retrying the same request cannot change the answer. 410 Gone dead-letters the delivery and switches the destination off, and its other deliveries are held, not lost. |
| **Circuit breaker** | After five failures in a row a destination is left alone for ten minutes, then exactly one probe goes out. If it succeeds the rest flow, and if it fails the breaker opens again. A permanent 4xx does not count, since the server is clearly alive. |
| **Replay** | Replay one delivery, one event (to the current routes or one chosen destination), or all dead letters of a destination. The original record is never rewritten, and replaying all dead letters twice does not queue them twice. |
| **Protection** | Per-source rate limits (only verified requests count), a 256 KB body limit, a pause switch that answers 503 so the provider retries instead of losing events, bounded memory, and a workspace per API key with no way to reach another's data. |

## How it is built

```
src/Relay.Engine   C#: signing, matching, hub (ingest, routing, delivery, circuit breaker, replay). No web code.
src/Relay.Api      ASP.NET Core minimal API with Swagger, workspaces, simulated receivers, background worker
tests/Relay.Tests  xUnit tests
```

The clock and the transport are injected, so every time-based rule is tested by moving a simulated clock instead of waiting. The hub is one lock around plain in-memory state: delivery attempts happen outside the lock, and an in-flight flag stops two workers from sending the same delivery.

## Tests

```bash
dotnet test
```

252 tests, including:

- both signature schemes: every malformed header, wrong secret, changed body, a forged timestamp, the exact edge of the tolerance window, several signatures at once and secret rotation;
- event-type and payload matching, including the near misses (`invoice.*` against `invoices.paid` and against `invoice` itself);
- ingest: duplicates by id and by body hash, the dedupe window edge, ids from headers, rate-limit window sliding to the second, size limit to the byte, pause and resume, and 50 parallel posts of one event producing exactly one;
- delivery: the exact retry times, each status class, `Retry-After`, a transport that throws, one destination failing without holding up another, 8 workers racing over 30 deliveries sending each once, and a cancelled run releasing what it had claimed;
- the circuit breaker: opening on the fifth failure and not the fourth, not sending the rest of a run once it opens mid-run, one probe after the cool-off, and closing again;
- replay and retention, and the API end to end: workspace isolation, a signed webhook through to a verified delivery, and refusals;
- a randomized test: 500 random actions across six seeds, after each of which every delivery's attempts, history and status must agree, no event reaches a destination twice, and filtered routes never leak.

I also broke the rules on purpose, 42 ways, to check the tests notice: accepting any signature, accepting stale timestamps, off-by-one at the tolerance edge, a prefix pattern matching its siblings, ignoring payload conditions, not detecting duplicates, forgetting ids too early or never, a paused source still accepting, an off-by-one rate limit, an old secret valid forever, retrying client errors, treating 429 as final, a 410 that does not switch the destination off, letting `Retry-After` shorten the wait or grow without limit, a circuit that opens one failure late or lets every delivery probe, removing the in-flight guard, a stale delivery signature, replaying deliveries still in progress, and more. 41 of them made tests fail. The 42nd (skipping the "destination is switched off" check while claiming deliveries) changes nothing a user can see, because a second identical check right before sending catches the same case, so it is kept as defense in depth.

## Scope

Data is kept in memory (a restart clears it, and the demo keeps the 100 most recent workspaces). The demo sends only to simulated receivers. A production version would add a database, per-destination ordering, real HTTP delivery with timeouts and SSRF protection, and a user interface.
