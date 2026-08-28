# Crawldad.Client — the typed .NET SDK

`Crawldad.Client` is the official typed .NET client for the Crawldad API. It is a thin, testable layer over
`HttpClient`: every method maps one documented endpoint, sends the tenant API key, deserializes the
`Crawldad.Contracts` wire types, and turns the API's typed rejection/problem bodies into typed exceptions. The
portal and any external integration consume the API exclusively through this SDK.

Ground truth for the surface is [docs/API.md](API.md) — this page is the .NET-specific how-to.

## Install

The package targets `net10.0` and depends only on `Crawldad.Contracts` and `Microsoft.Extensions.Http`.

```xml
<PackageReference Include="Crawldad.Client" />
```

## Register with DI

`AddCrawldadClient` registers `CrawldadClient` as a typed `HttpClient` client (so it participates in
`IHttpClientFactory` handler pooling). Options are validated eagerly, so a missing base URL or API key fails at
startup rather than on the first call. Keep the API key out of source — bind it from configuration or a secret store.

```csharp
services.AddCrawldadClient(options =>
{
    options.BaseUrl = new Uri("https://api.crawldad.io/");
    options.ApiKey  = configuration["Crawldad:ApiKey"]!;
});
```

Then inject `CrawldadClient` anywhere:

```csharp
public sealed class ScrapeService(CrawldadClient crawldad)
{
    // ... use crawldad ...
}
```

The call returns the underlying `IHttpClientBuilder`, so you can layer message handlers (retries, logging) onto the
client. To construct one without DI, `new CrawldadClient(httpClient, new CrawldadClientOptions { BaseUrl = …, ApiKey = … })`.

## Authentication

Every request is sent with `Authorization: Bearer <api-key>` — the API's primary convention. The key comes from
`CrawldadClientOptions.ApiKey`; it is never logged by the SDK.

## Run a payload

`POST /runs` answers in one of two shapes, which `StartRunResult` captures: a **synchronous** run finishes with a
terminal `RunResponse` (`IsCompleted == true`), while an **async** run — or one auto-upgraded past the sync window, or
queued behind the concurrency cap — returns an accepted `RunStateResponse`.

```csharp
using System.Text.Json;

// An inline payload document + its inputs (backend binding, parameters).
var payload = JsonSerializer.Deserialize<JsonElement>("""
    { "crawldad": "1", "name": "demo",
      "config": { "backend": "input.backend" },
      "steps": [ { "goto": { "url": "https://example.org/" } },
                 { "set": { "var": "landed", "value": "pageUrl()" } } ],
      "result": "{ url: landed }" }
    """);
var inputs = JsonSerializer.Deserialize<JsonElement>("""
    { "backend": { "adapter": "browserless" } }
    """);

// Synchronous: the terminal result comes straight back.
var result = await crawldad.CreateInlineRunAsync(payload, inputs);
if (result.IsCompleted && result.Status == RunStatus.Succeeded)
{
    JsonElement shaped = result.Completed!.Result!.Value; // your caller-shaped result
}

// Asynchronous: fire-and-poll.
var accepted = await crawldad.CreateInlineRunAsync(payload, inputs, async: true);
RunStateResponse state = await crawldad.GetRunAsync(accepted.RunId);

// A pinned managed payload instead of an inline one:
var pinned = await crawldad.CreatePinnedRunAsync(payloadId, revision: 3, inputs: inputs, async: true);
```

Other run operations: `CancelRunAsync`, `EraseRunAsync`, `ReplayRunAsync`, `GetRunTimelineAsync`, `GetRunDriftAsync`,
`GetRunScreenshotAsync`, and `GetQueueStatsAsync`.

## List runs

`ListRunsAsync` reads `GET /runs` — a filterable, offset-paginated page of the tenant's runs, **newest first** (by
`startedAt`, run id as the stable tiebreaker). It returns a `RunListResponse` of lightweight `RunListItem` rows (list-view
fields only — the full result, timeline, and drift stay on the per-run surfaces). Every filter is optional and
AND-combined; paging defaults and clamps are applied server-side, so a null or out-of-range value simply takes the
default (`page` floors at 1, `size` defaults to 25 and clamps to 1..100).

```csharp
// Newest 25 runs (the defaults):
RunListResponse page = await crawldad.ListRunsAsync();

// Second page of failed runs for one payload, in an August window:
var failures = await crawldad.ListRunsAsync(
    status: RunStatus.Failed,
    payloadId: payloadId,
    from: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
    to:   new DateTimeOffset(2026, 8, 31, 23, 59, 59, TimeSpan.Zero),
    page: 2,
    size: 50);

foreach (RunListItem run in failures.Runs)
{
    // run.Status, run.DurationMs, run.PayloadName (+ run.PayloadRevision, or run.Inline),
    // run.Region, run.Stats (terminal-only), run.Failure (failed-only).
}

bool morePages = failures.HasMore; // true when a further page exists past this one
int matched    = failures.Total;   // the count across the whole filtered set, not just this page
```

A running/queued row omits the terminal-only fields (`DurationMs`, `Stats`, `Failure`) and `Region`. The listing
projection is lag-tolerant, so a just-created run may take a moment to appear.

## Stream the trace (SSE)

`StreamRunEventsAsync` reads `GET /runs/{id}/events` as an `IAsyncEnumerable<RunEventFrame>`. The server backfills the
durable stream then follows the live tail until a terminal frame closes it. Keepalive comment frames are consumed
silently; each frame's `Id` is the durable stream version — pass it back as `lastEventId` to resume exactly on
reconnect.

```csharp
await foreach (var frame in crawldad.StreamRunEventsAsync(runId, cancellationToken: ct))
{
    Console.WriteLine($"{frame.Id}  {frame.EventType}");
    if (frame.EventType == "Navigated")
    {
        var data = frame.DataAs<JsonElement>(); // the (already-scrubbed) event body
    }

    if (frame.IsTerminal) // RunSucceeded / RunFailed / RunCancelled — the stream then closes
    {
        break;
    }
}

// Resume after a dropped connection from the last id you saw:
await foreach (var frame in crawldad.StreamRunEventsAsync(runId, lastEventId: lastSeenId, ct))
{
    // continues at exactly the next frame — no loss, no duplication
}
```

## Error handling

API-level rejections surface as typed exceptions (never a raw `HttpRequestException`), all deriving from
`CrawldadException` (which carries the HTTP `StatusCode`):

| Exception | When |
|---|---|
| `CrawldadRunRejectedException` | A run control-surface rejection — `400` (unrunnable pinned reference, `inline_not_replayable`), `429 queue_depth_exceeded`, `409 run_still_active`. Inspect `.Code`. |
| `CrawldadPayloadInvalidException` | A payload failed schema/semantic validation on save/revise (`400`). Inspect `.Errors`. |
| `CrawldadValidationException` | A request body or name slug failed boundary validation (`400`, RFC 7807). Inspect `.Errors`. |
| `CrawldadNotFoundException` | The resource does not exist for this tenant (`404`). |
| `CrawldadUnauthorizedException` | No valid API key (`401`). |
| `CrawldadApiException` | Any other unexpected status; `.ResponseBody` carries the raw body. |

A run that *starts then faults* is not an exception — it is still `200`/`202` with `RunStatus.Failed` and a typed
`Failure` on the response, exactly as documented in [docs/API.md](API.md).

## Other surfaces

- **Payloads:** `SavePayloadAsync`, `RevisePayloadAsync`, `RenamePayloadAsync`, `ArchivePayloadAsync`,
  `ListPayloadsAsync`, `GetPayloadAsync`, `GetPayloadRevisionAsync`, `DiffPayloadAsync`, `GetPayloadDriftStatusAsync`.
- **Webhooks:** `RegisterWebhookAsync`, `ListWebhooksAsync`, `GetWebhookDeliveriesAsync`, `UnregisterWebhookAsync`.
  `ListWebhooksAsync` rows carry an additive `LastDelivery` summary (the endpoint's most recent delivery outcome, absent
  until first delivered); `GetWebhookDeliveriesAsync(name, limit?)` reads that endpoint's recent attempts newest-first —
  each attempt (retries included) a distinct row, with the observed status, latency, event type, and run id.
- **Fixtures:** `RecordFixtureAsync`, `ListFixturesAsync`, `GetFixtureAsync`, `DeleteFixtureAsync`.
- **Browsers:** `RegisterBrowserAsync`, `ListBrowsersAsync`, `UnregisterBrowserAsync`.

Every method takes a `CancellationToken` and is async-only.

## Tenant & usage

Two read-only, tenant-scoped calls back the portal's account area (both derive the tenant from the API key — there is
no id parameter):

```csharp
// GET /tenant — the authenticated tenant's profile.
TenantProfileResponse tenant = await crawldad.GetTenantAsync();
// tenant.TenantId, tenant.DisplayName, tenant.Tier (optional), tenant.SlotAllowance, tenant.QueueDepthAllowance

// GET /usage — live usage against guardrails (approximate by design, not a billing ledger).
UsageResponse usage = await crawldad.GetUsageAsync();
// usage.Slots (InUse/Allowance), usage.Queue (Depth/Sampled/P95WaitMs),
// usage.RunsStartedThisMonth, usage.Events (Guardrail/Sampled/Avg/Max)
```

`GetTenantAsync` is the cheapest authenticated round-trip, so it doubles as a **key-validity probe**: a wrong or revoked
key surfaces as `CrawldadUnauthorizedException` (`401`) rather than a valid response — which is exactly how the portal
verifies a pasted key before storing it.

## Manage your API keys

A tenant manages its **own** keys with its own key — list, mint, rotate, revoke — so automation, an MCP server, or an
agent can rotate its credentials without an operator. The raw key comes back **exactly once**, from mint and rotate; it
is never listed and the SDK never logs it. See [docs/API.md §22](API.md#22-tenant-self-service-api-keys--tenantkeys) for
the wire shapes.

```csharp
// GET /tenant/keys — prefixes + metadata only; the key you're calling with is flagged Current.
TenantApiKeyList keys = await crawldad.ListTenantKeysAsync();
foreach (TenantApiKeyInfo k in keys.Keys)
{
    // k.KeyId, k.Prefix, k.Label, k.CreatedAt, k.LastUsedAt, k.Active, k.Current
}

// POST /tenant/keys — mint a key; the raw value is on the result ONCE. Store it now.
TenantApiKeyCreated minted = await crawldad.MintTenantKeyAsync(label: "ci");
string raw = minted.ApiKey; // shown once — never retrievable again

// POST /tenant/keys/{id}/rotate — mint a replacement + revoke the old key atomically.
// The anti-lockout way to replace the key you're currently using: swap to the returned key.
TenantApiKeyCreated rotated = await crawldad.RotateTenantKeyAsync(minted.KeyId);

// DELETE /tenant/keys/{id} — revoke a key you no longer need.
await crawldad.RevokeTenantKeyAsync(someOldKeyId);
```

Guardrails (all server-enforced): the surface is **registry tenants only** — an env-configured tenant is a `400`
(`CrawldadApiException`, `self_service_unavailable`). Revoking your **last active key**, or the key this client is
authenticating with, is refused with a `409` (`CrawldadApiException`) — **rotate** it instead. A rotate is always safe:
it mints the replacement before revoking the old key, so there is never a moment with no live key.

## Billing

Three tenant-scoped calls back the portal's billing card. The SDK only ever receives a **URL to follow** — it never holds
a Stripe secret, and it cannot change a tenant's plan (that lands only via a Stripe subscription webhook the API receives
out of band). See [docs/API.md §22](API.md) for the wire shapes and the scaffolding status (fake gateway in
Development/tests; a fail-closed stub in Production until Stripe is wired).

```csharp
// GET /billing/config — is billing configured, the current tier, and the tier catalog to render.
BillingConfigResponse config = await crawldad.GetBillingConfigAsync();
if (!config.Configured)
{
    // Render a friendly "billing not yet available" state — do not call the session methods below.
}

// POST /billing/checkout-session — a redirect URL to open Stripe Checkout for a self-serve tier upgrade.
BillingSessionResponse checkout = await crawldad.CreateCheckoutSessionAsync("team");
// redirect the browser to checkout.Url

// POST /billing/portal-session — a redirect URL to open the Stripe hosted Billing Portal.
BillingSessionResponse portal = await crawldad.CreatePortalSessionAsync();
// redirect the browser to portal.Url
```

`CreateCheckoutSessionAsync` throws `CrawldadValidationException` for a tier that is not purchasable (`400`), and any
billing call throws `CrawldadApiException` when billing is not yet available for the deployment (`503`) — the portal
catches either and shows the "not yet available" state rather than a 500.
