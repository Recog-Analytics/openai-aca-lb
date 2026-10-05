# Dashboard design

Status: agreed, not implemented. Do not deploy yet.

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
    { "id", "startedAt", "caller", "modelKey", "zone", "streaming", "status", "durationMs",
      "attempts": [{ "deploymentId", "region", "tier", "status", "ttfbMs", "outcome", "retryReason" }] }
  ],
  "counts": [                           // all requests since last batch, sampled or not
    { "deploymentId", "outcome", "count" }
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
- Keeps the last 2 minutes of request events and the latest state per replica. A replica is dropped after 30 s without a batch.
- `GET /api/stream` (SSE): first a full snapshot, then one merged delta per second.
  - **Merged state per deployment:** worst state across replicas, plus a per-replica breakdown (for example 3/4 see Open).
  - **Sampled request events:** at most 100 per second to browsers.
  - **Per-deployment counts:** requests/s and outcome mix.
- Serves the built web app from `wwwroot`.
- `/healthz` anonymous.

## Auth

- Container Apps built-in authentication (Easy Auth), Microsoft Entra provider, single-tenant app registration. Any user in the tenant can sign in. No group restriction.
- Browser routes require login. `/ingest` requires a bearer token whose `oid` equals the configured LB managed identity object ID. Read it from the `X-MS-CLIENT-PRINCIPAL` header Easy Auth injects; reject everything else with 403.
- In Development (local), Easy Auth is absent. `Dashboard:DevAuth=true` accepts the static dev token on `/ingest` and skips login. It fails startup outside Development.

## Web app (`dashboard/web`, TypeScript, bun, Vite, React)

- **Rendering:** Motion for UI springs and layout. PixiJS (WebGL) for the request particles and links.
- **One scene, continuously retargeted.** New data changes targets; motion keeps position and velocity (springs, no restarts). Elements keep identity across updates.
- **Layout:** callers → LB → deployment nodes grouped by tier (columns) and region.
  - Node size follows weight.
  - Node state: Healthy (calm), Throttled (blue cooldown ring that drains), Degraded (amber pulse), Open (dimmed, broken link), half-open (single probe dot), Disabled (greyed).
- **Request particles:** each sampled request travels its attempt chain. A failed attempt bounces back in red and continues to the next deployment. Streaming requests leave a trail until they finish. Particle rate is capped; counts drive link thickness.
- **Interaction:**
  - Hover a node: weight, tier, zone, p95, req/s, outcome mix, per-replica states.
  - Click a particle or feed row: attempt chain detail.
  - Filters: model, zone, caller.
  - Pause/scrub the last 2 minutes.
- **Side feed:** latest requests with compact attempt chains, linked to the scene.
- **Look:** dark-first, also light. Calm at rest; motion only means something changed. Respect `prefers-reduced-motion`. Readable on a laptop and a wall screen.

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
