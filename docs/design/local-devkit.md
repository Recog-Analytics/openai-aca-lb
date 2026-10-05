# Local devkit design

Status: agreed, not implemented. Goal: run the load balancer (LB) locally against simulated Azure, control failure modes per region, and watch routing live.

## Phase A: LB changes

1. `Discovery:ArmEndpoint` (default `https://management.azure.com`). Pagination links must match its scheme and authority. HTTP is allowed only in Development.
2. `Discovery:RefreshInterval` (default 5 min).
3. `Azure:Credential` = `ManagedIdentity` (default) | `Fake`. `Fake` returns a static token for ARM and backends. Outside the `Development` environment, `Fake` fails startup.
4. `GET /admin/requests?limit=N`: authenticated like `/admin/state`, in-memory ring buffer of the last 500 requests per replica. Each entry has:
   - id, start time, caller name, requested model, resolved model key, zone, streaming flag
   - final status, total duration
   - operation, API version, request bytes, requested output limit
   - attempts: deployment, account, region, tier, status, TTFB, health outcome, retry reason, backend request ID, and for failures the Azure error code and message (max 300 characters)

   Never store bodies or keys.
5. A `Development` profile in `launchSettings.json` / `appsettings.Development.json` pointing at the devkit (section B), with fake credential, 5 s refresh, and files from `tools/devkit/config/`.

## Phase B: devkit (`tools/devkit`, ASP.NET, in the solution)

### Mock ARM (port 5100)

- Serves accounts, deployments and subscription locations for the API versions the LB uses, from `tools/devkit/config/scenario.yaml`.
- Default scenario:
  - subscription `00000000-0000-0000-0000-000000000001`
  - accounts in swedencentral, francecentral, germanywestcentral, eastus2, plus one global account
  - models `gpt-4o@2024-11-20` (regional Standard / DataZoneStandard, mixed capacities, one PTU deployment, one GlobalStandard) and `text-embedding-3-large@1` (not in every region)
- Each account endpoint is `http://localhost:<port>/` with its own port (5101+).
- A deployment can be removed or added at runtime, so the next LB refresh sees it.

### Fake accounts (one Kestrel listener per account)

- `POST /openai/deployments/{name}/chat/completions` and `/openai/v1/chat/completions`, non-streaming and SSE streaming, OpenAI-shaped bodies.
- `POST .../embeddings`.
- Unknown deployment → 404 with `DeploymentNotFound`.
- Response headers: `x-ratelimit-remaining-tokens`, and `x-devkit-account` / `x-devkit-deployment` so the UI can confirm the target.

### Controls (per account, with per-deployment overrides)

| Control | Effect |
|---|---|
| `ttfbMs`, `jitterMs` | delay before headers |
| `tokensPerSecond`, `outputTokens` | streaming pace and length |
| `errorRate` | share of requests answered 500 |
| `throttleRate`, `retryAfterMs` | share answered 429 with `retry-after-ms` |
| `outage` | listener stopped (connection refused) |
| `missing` | deployment answers 404 `DeploymentNotFound` |

- Control API: `GET/PUT /devkit/controls`.
- Presets: `POST /devkit/presets/{name}` with `recover-all`, `sweden-slow`, `france-throttled`, `eastus2-outage`, `eu-down`.

### Traffic generator

- `PUT /devkit/traffic`: requests/s, concurrency, model, streaming share, zone header, on/off.
- Uses a dev caller key against the LB.

### UI (`GET /` on 5100)

- One static HTML file with inline JS and CSS. No framework, no build step.
- Polls the LB `/admin/state` and `/admin/requests` (through the devkit, which holds the key) and `/devkit/controls` about once per second.
- **Deployments panel:** one card per deployment, showing region, tier, zone, weight, state (Healthy / Throttled / Degraded / Open / Disabled), p95 TTFB, and its controls.
- **Request feed:** newest first, with the attempt chain such as `swedencentral 429 → francecentral 200 · 1.2 s`, filterable by model and outcome.
- **Header bar:** traffic controls, presets, and per-region request share over the last minute.
- Readable in light and dark mode. Works without the LB running (shows "LB unreachable").

### Config and run

- `tools/devkit/config/`:
  - `scenario.yaml`
  - `callers.yaml` (dev key hash; the plain dev key goes in the devkit's appsettings only)
  - `overrides.yaml`
- `tools/devkit/run.sh`: starts the devkit and the LB (Development) and prints the UI URL. Ctrl+C stops both.
- README section "Local testing".

### Tests

- Fake account behavior per control.
- Mock ARM contract, shared with the LB's ArmClient tests where practical.
- One end-to-end test: devkit + LB in-process. Apply `eastus2-outage`, verify requests route elsewhere and the account circuit opens.
