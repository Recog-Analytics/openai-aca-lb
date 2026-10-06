# Dashboard design

Status: implemented. Phases D1 to D6 run in production; the phase D7 changes (history, request context, scaling to 70+ deployments) still need deployment and live verification.

A live, animated view of how the load balancer (LB) routes requests, for production and local use. It is a separate Container App. Anyone in the Entra tenant can view it.

## Architecture

```
LB replica 1..N ──POST /ingest (1 s batches)──▶ dashboard service ──SSE /api/stream──▶ browsers
                                                 (merges replicas,         (Entra login via
                                                  serves the web app)       Container Apps auth)
```

Locally, `tools/devkit/run.sh` also starts the dashboard service (port 5200) and points the LB at it. The devkit keeps only the mock and its control panel (phase D3).

## Event contract (`/ingest`, JSON)

```jsonc
{
  "replica": "lb--abc12-7f9d",          // CONTAINER_APP_REPLICA_NAME, else host name
  "sentAt": "2026-10-04T10:00:01Z",
  "state": {                            // full health snapshot of this replica
    "refreshedAt": "…",
    "deployments": [{ "id", "account", "deployment", "region", "modelKey", "tier", "zone",
                      "weight", "state", "p95TtfbMs", "accountOpen" }]
  },
  "requests": [                         // completed since last batch, max 200 (sampled)
    { "id", "startedAt", "caller", "modelKey", "zone", "streaming", "status", "durationMs", "operation", "apiVersion", "requestBytes", "maxOutputTokens",
      "attempts": [{ "deploymentId", "region", "tier", "status", "ttfbMs", "outcome", "retryReason", "errorCode", "errorMessage", "backendRequestId" }] }
  ],
  "counts": [                           // all requests since last batch, sampled or not
    { "deploymentId", "outcome", "count" }
  ],
  "routes": [                           // all requests since last batch, unsampled, grouped by route; absent from older LBs
    { "caller", "modelKey", "zone", "status", "count", "poolKind", "pool",
      "hops": [{ "deploymentId", "outcome" }] }
  ]
}
```

The same record shape as `/admin/requests`. No bodies, keys or tokens.

## LB side (publisher)

- Config: `Dashboard:IngestUrl` (empty = off), `Dashboard:Credential` follows `Azure:Credential`.
- Background service, 1 s interval, bounded queue of 10 000 request events (drop oldest, count drops).
- Fire-and-forget: a failed or slow push never affects proxying. 5 s timeout, no retries; the next batch carries fresh state.
- Auth: a managed identity token for the dashboard app registration audience. With the `Fake` credential, the static dev token is used.

## Dashboard service (`dashboard/server`, ASP.NET, in the solution)

- `POST /ingest`: accepts batches only from the LB identity (see Auth).
- Keeps the latest state per replica. A replica is dropped after 30 s without a batch.
  Each live replica gets a stable number (`replicaNumbers`, "Replica N"): the lowest positive integer no other known replica holds.
- `GET /api/stream` (SSE): first a full snapshot, then one merged delta per second.
  - **Merged state per deployment:** worst state across replicas, plus a per-replica breakdown (for example 3/4 see Open).
  - **Sampled request events:** at most 100 per second to browsers.
  - **Per-deployment counts:** requests/s and outcome mix.
  - **Snapshot:** 2 minutes of requests and route ticks, the day `summary` (one bucket per closed minute: total, served, retried, failed, refused, worst state, unhealthy deployments), and `retention` (earliest per-second and per-minute instants). A delta whose tick closes a minute carries that one bucket in `summary`.
- History (`DashboardHistory`, in memory, single replica, lost on restart):
  - Per-second route counts and merged states for 1 hour. States are stored change-only, with a baseline for the oldest retained second.
  - Per-minute aggregates for 24 hours: route counts summed, worst state, non-healthy seconds, accountOpen/halfOpen and the largest p95 per deployment.
  - Request events for 1 hour: every notable one (status >= 400 or more than one attempt) and a uniform sample of at most 4 others per second of receipt time. Hard cap 20 000 and an estimated 32 MiB; over it, the oldest others leave first, then the oldest notable ones. At 40 requests/s with 2 % notable, the hour holds about 17 300 events.
  - The live snapshot keeps its own unthinned 2-minute request queue, capped at 12 000 events (the 100 browser samples a second over two minutes) and an estimated 32 MiB.
  - The latest record of each deployment seen in the last 24 hours (at most 5 000), so a removed deployment still renders.
  - Route shapes (caller, model, pool, zone, status, attempt chain) and their strings are interned. Ingest rejects identifiers over 512 characters, so no ID is ever cut (an ARM deployment ID is about 170). A tick stores (shape, count) pairs. Beyond 10 000 live shapes, a new shape counts under caller `(other)` with its status and attempt count.
  - Hard caps, each dropping the oldest data first: 3 600 seconds, 200 000 per-second route entries, 100 000 state changes; 1 440 minutes, 400 000 per-minute route entries, 100 000 per-minute states.
  - Memory: worst case at every cap about 180 MB (shapes about 55 MB, a string table of 20 000 strings up to 512 characters about 20 MB, retained requests 32 MiB, live 2-minute requests 32 MiB, per-minute 13 MB, per-second 11 MB, deployments about 9 MB). Typical (71 deployments, 40 route entries/s, a few non-healthy deployments, 40 requests/s) about 30 MB, mostly request events.
- `GET /api/history?from&to&resolution` (same auth as the stream): buckets of `resolution` seconds (1, 10, 30, 60, 300, 600, 1800, 3600) ending on multiples of the resolution.
  - 400 for a missing or unparsable instant, an unknown resolution, `from >= to`, or more than 1 500 buckets in the requested range. The range is then clamped to retention.
  - Resolutions of 1 to 30 s read per-second data only. From 60 s, a range that starts before the per-second data reads closed minutes, then per-second data after the last closed minute.
  - Each bucket: present seconds, route counts summed per route, and per non-healthy deployment the worst state, non-healthy seconds, flags and largest p95.
  - Up to 1 000 sampled requests received in the range: notable ones first (status >= 400 or a retry), spread evenly when there are more, then others; oldest first.
  - Every deployment seen in the range, with its latest record.
- Brotli/gzip response compression for JSON and static files. SSE (`text/event-stream`) is not compressed, so each frame still flushes.
- Serves the built web app from `wwwroot`.
- `/healthz` anonymous.

## Auth

- Container Apps built-in authentication (Easy Auth), Microsoft Entra provider, single-tenant app registration. Any user in the tenant can sign in. No group restriction.
- Browser routes require login. `/ingest` requires a bearer token whose `oid` equals the configured LB managed identity object ID. Read it from the `X-MS-CLIENT-PRINCIPAL` header Easy Auth injects; reject everything else with 403.
- In Development (local), Easy Auth is absent. `Dashboard:DevAuth=true` accepts the static dev token on `/ingest` and skips login. It fails startup outside Development.

## Web app (`dashboard/web`, TypeScript, bun, Vite, React)

- **Rendering:** Motion for UI springs and layout. PixiJS (WebGL) for the request particles and links.
- **One scene, continuously retargeted.** New data changes targets; motion keeps position and velocity (springs, no restarts). Elements keep identity across updates.
- **Layout:** callers → LB → group → deployment. Groups are regions (default) or models (toggle). Built for 70+ deployments (phase D7):
  - Healthy deployments under 0.5 % of the window's requests fold into one "N quiet" lane per group (unfold above 1 %); a click lists them.
  - Problems never fold into quiet lanes. Four or more deployments of one group with the same problem share one lane ("19 deployments down"); a problem lane is held 15 s after recovery.
  - Unused models gather in one "N idle models" group in the model view.
  - Label density (3, 2 or 1 lines) follows lane height; a lane is never shorter than its label (`labelHeight` in `layout.ts`, mirrored in `styles.css`), so labels never overlap. When one-line labels do not fit, the scene scrolls. `layout.test.ts` asserts this for the production inventory at 1280×720 to 3840×2160 in both views.
  - Evidence for region first: both views fit calm production traffic at 1600×900, but in an account outage the region view shows one red lane and still fits (or scrolls a few lines), while the model view needs one problem lane in every model group (about 1000 px of scroll at 1600×900 for the Sweden outage). Slowness and throttling are per account too, so the region view keeps them in one place.
  - Node state: Healthy (calm), Throttled (blue cooldown ring that drains), Degraded (amber pulse), Open (dimmed, broken link), half-open (single probe dot), Disabled (greyed).
- **Request particles:** each sampled request travels its attempt chain. A failed attempt bounces back in red and continues to the next deployment. Streaming requests leave a trail until they finish. Particle rate is capped; counts drive link thickness.
- **Interaction:**
  - Hover a node: weight, tier, zone, p95, req/s, outcome mix, per-replica states.
  - Click a particle or feed row: attempt chain detail.
  - Filters: model, zone, caller.
  - Time ranges 2 min, 15 min, 1 h, 24 h; the figure window ends at the playhead. Longer ranges fetch `/api/history` buckets, which act as long ticks for the same exact funnel; the scene shows each deployment's worst state in the bucket under the playhead. A "Not live" band and a blue stage outline mark the past.
  - Human names: Azure region display names, "Replica N", pool suffixes ("gpt-4o-mini · public"); raw IDs only as secondary text.
  - Request detail: operation, API version, body size, output limit, streaming, caller, attempts with backend error code, message and request ID.
- **Side feed:** latest requests with compact attempt chains, linked to the scene.
- **Look:** the Recog brand from the ops portal (shadcn tokens, Geist, 0.625rem radius base, Recog mark, branded loading page), dark and light, WCAG AA text. State colours map onto the brand palette. Calm at rest; motion only means something changed. Respect `prefers-reduced-motion`. Readable on a laptop and a wall screen.

## Build and deploy (not deployed in this work)

- Dockerfile for the dashboard: bun builds the web app, then the .NET publish copies it to `wwwroot`. Separate from the LB image.
- Bicep: the dashboard Container App (external ingress, min 1 / max 1 replica), Easy Auth config, app registration (Microsoft Graph Bicep extension if it compiles, otherwise an idempotent `az ad app` step in the post-provision hook), and the LB `Dashboard:IngestUrl` setting.
- `azure.yaml`: second service.

## Phases

- **D1, data path:**
  - LB publisher.
  - Dashboard service: ingest, merge, SSE, auth modes.
  - A minimal placeholder page that shows the raw stream.
  - `run.sh` starts the dashboard locally.
  - Tests: merge rules, replica expiry, sampling, auth guard, publisher isolation.
- **D2, web app:** the scene described above, against the local stack. Iterate on feel.
- **D3, packaging:**
  - Dockerfile, Bicep, `azure.yaml` and docs.
  - Trim the devkit UI to the control panel, with a link to the dashboard.
  - Bicep compiles; nothing is deployed.
