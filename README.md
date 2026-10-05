# Azure OpenAI load balancer

A .NET 10 proxy built with YARP. It discovers Azure OpenAI deployments and routes requests by model, quota, health, and data residency.

Clients use one endpoint and a proxy-issued API key. The proxy authenticates to Azure OpenAI with its managed identity.
It never forwards client credentials or falls back to a different model.
See [the design](docs/design/load-balancing.md) for the specification and [CHANGELOG](CHANGELOG.md) for changes.

## Runtime configuration

Configure discovery through appsettings or environment variables:

```json
{
  "Discovery": {
    "Scopes": [
      "11111111-1111-1111-1111-111111111111",
      "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/openai"
    ],
    "OverridesFilePath": "/mnt/config/overrides.yaml",
    "CallersFilePath": "/mnt/config/callers.yaml"
  }
}
```

| Environment variable | Purpose |
| --- | --- |
| `Discovery__Scopes__0`, `Discovery__Scopes__1`, … | Subscription GUIDs, subscription ARM IDs, or resource-group ARM IDs. |
| `Discovery__OverridesFilePath` | Path to the routing override YAML file. |
| `Discovery__CallersFilePath` | Path to the caller YAML file. |
| `Discovery__ArmEndpoint` | ARM origin. Default: `https://management.azure.com`. HTTP requires Development. |
| `Discovery__RefreshInterval` | Positive .NET TimeSpan between discovery refreshes. Default: `00:05:00`. |
| `Azure__Credential` | `ManagedIdentity` (default), or `Fake` in Development only. |
| `AZURE_CLIENT_ID` | User-assigned managed identity client ID. Omit for a system-assigned identity. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Azure Monitor export destination. |
| `Operations__SlackWebhookUrl` | Optional HTTPS Slack webhook. An empty value disables alerts. |

Supply at least one scope and both file paths. Relative paths resolve against the application content root.
Both files must exist. Empty files are valid; an empty callers file grants no access.
Discovery settings are startup settings. File contents reload at startup and on each refresh interval.

Discovery reads `OpenAI` and `AIServices` accounts through the configured ARM origin.
Pagination links must retain that origin's scheme and authority. ARM redirects are not followed.
It keeps successful deployments with supported SKUs and deduplicates overlapping scopes.
The managed identity needs Reader on discovery scopes and access to subscription location metadata.
It also needs Cognitive Services OpenAI User on every backend account.
Backend tokens use `https://cognitiveservices.azure.com/.default` and remain cached until two minutes before expiry.

Each successful refresh publishes the routing table and caller configuration together.
ARM, file, and validation failures retain the last successful snapshot and log an error.
Deployment additions, removals, capacity changes, and weight changes produce logs.
Unknown geography excludes a deployment with a warning, including global deployments.
A region override can supply the zone. Global SKUs always keep the `global` zone.
Azure public cloud is the supported environment.

## Caller keys and data residency

Callers authenticate with `api-key: <key>` or `Authorization: Bearer <key>`.
The key contains `lbk_` followed by 32 random bytes encoded as base64url, without padding.
Missing, unknown, or malformed credentials return 401.
Identical credentials in both headers are accepted; conflicting credentials are rejected.
The proxy strips both headers before backend forwarding.

Generate a key and its hash with Python:

```sh
python3 - <<'PY'
import hashlib
import secrets

key = "lbk_" + secrets.token_urlsafe(32)
print("Caller key:", key)
print("Configuration hash: sha256:" + hashlib.sha256(key.encode()).hexdigest())
PY
```

Give the key to the caller through your secret delivery process. Store only its hash in the callers file:

```yaml
callers:
  - name: orchestrator
    zones: [eu, global]
    keyHashes:
      - "sha256:REPLACE_WITH_64_HEXADECIMAL_DIGITS"
```

Replace the example hash before deployment. Caller names and key hashes must be unique.
Each caller requires at least one zone and one hash. Hash comparison runs in constant time.
Telemetry identifies the caller by name and never records the key or hash.

The first zone is the default. `x-lb-data-zone` selects another zone allowed for that caller.
A zone outside that list returns 403. `global` allows deployments from every zone.
Other zones allow only deployments with a matching zone, regardless of tier.

Rotate a key in this order:

1. Add the new hash to the caller configuration.
2. Wait for the updated file to reach each replica and refresh successfully.
3. Update the caller to use the new key.
4. Remove the old hash and wait for another successful refresh.

## Routing overrides

Use the override file for version defaults, aliases, exclusions, manual drains, weight adjustments, and region corrections:

```yaml
defaultVersions:
  gpt-4o: "2024-11-20"
aliases:
  chat: gpt-4o@2024-11-20
  embedding: text-embedding-3-large
  mini-public: llm-gpt-4omini-public
exclude:
  - account: oai-legacy-westeurope
  - account: oai-swedencentral
    deployment: gpt4o-test
deployments:
  - account: oai-francecentral
    deployment: gpt4o
    tier: 1
    weightMultiplier: 0.5
    disabled: true
regions:
  switzerlandnorth: { zone: eu }
modelHealth:
  gpt-4o@2024-11-20:
    degradedFloorSeconds: 2
    degradedThresholdSeconds: 6
```

Account and deployment selectors use Azure resource names, with case-insensitive matching.
Exclusions remove deployments from the table. `disabled: true` retains a deployment but stops traffic.
Exclusions take precedence over deployment overrides. Zero weight receives no traffic.
An override cannot include batch, unknown, or unsuccessful deployments.
Region names are case-insensitive. Model names and versions are case-sensitive.
Unknown properties, duplicate YAML keys, duplicate deployment overrides, and invalid values fail the refresh.

An alias maps a client name to a model key (`name` or `name@version`) or to a deployment name.
Alias names are case-insensitive, unique, and cannot contain `@`.
An alias that equals a discovered model name fails the refresh with a configuration error.
An alias whose target is not discovered returns 400 at request time.

`degradedFloorSeconds` defaults to 2. The optional `degradedThresholdSeconds` applies when no peer has enough TTFB samples.
Both settings require finite, positive seconds. `modelHealth` keys must include a version.

## Client requests

Address a model by its model name, optionally followed by `@version`, by an alias, or by a deployment name:

- Azure API: `/openai/deployments/gpt-4o@2024-11-20/chat/completions?api-version=...`.
- OpenAI v1 API: `/openai/v1/chat/completions` or `/v1/chat/completions`, with `"model": "gpt-4o@2024-11-20"`.

The proxy resolves the requested name in this order:

1. Model key. An unversioned model uses the configured default, or the only discovered version.
2. Alias from the override file.
3. Deployment name. The pool contains every deployment with exactly that name, across accounts and regions (case-insensitive).
4. Otherwise 400.

A known model name with an unavailable default or an ambiguous version returns 400 with available versions.
It does not fall through to aliases or deployment names.
A deployment-name pool never includes deployments with other names, even for the same model.
For example, `llm-gpt-4omini-public` and `llm-gpt-4omini` stay separate pools.
If deployments that share one name serve different model keys, the refresh logs a warning and the pool includes all of them.
Health, weights, and selection work the same for both pool kinds.
Degradation peers are always the deployments with the same model key.

**Migration from deployment names.** Existing clients that call `/openai/deployments/<deployment-name>/...` keep working.
Their requests go to the pool of deployments with that name.
To route a legacy name to a whole model, or to a different name, add an alias.
The proxy rewrites the deployment path segment or top-level v1 `model` field to the selected Azure deployment name.
It normalizes `/v1/...` to `/openai/v1/...`.
Duplicate top-level v1 fields, decoded dot segments, and backslashes are rejected.
Other body fields retain their meaning. Operation suffixes are preserved without a second decode.

Use a proxy key with the OpenAI Python SDK:

```python
import os
from openai import OpenAI

client = OpenAI(
    base_url=os.environ["LOAD_BALANCER_URL"].rstrip("/") + "/openai/v1",
    api_key=os.environ["LOAD_BALANCER_KEY"],
)
response = client.chat.completions.create(
    model="gpt-4o@2024-11-20",
    messages=[{"role": "user", "content": "What is the first letter of the alphabet?"}],
)
print(response.choices[0].message.content)
```

Azure SDK clients can use the proxy URL as `azure_endpoint` and the proxy key as `api_key`.
Use a model name, an alias, or an existing deployment name as the Azure deployment segment.
Responses include `x-lb-deployment`, `x-lb-region`, and `x-lb-attempts` for the selected backend and attempt count.

The proxy buffers request bodies for retries, up to `RequestPipeline__MaximumBodyBytes` (default 26 MiB, maximum 1 GiB).
The default covers 25 MB audio uploads. Larger bodies return 413.
Azure-style request bodies, including `multipart/form-data` audio uploads, are preserved verbatim with their content type.
v1 requests need a JSON body with a `model` field, so v1 multipart uploads are not supported. SSE responses stream as chunks arrive without response buffering.
Backend redirects pass through; the proxy does not follow them.

### Selection and retries

| Tier | SKUs | Zone |
| --- | --- | --- |
| 0 | `ProvisionedManaged`, `DataZoneProvisionedManaged`, `GlobalProvisionedManaged` | Region geography, or `global` for global SKUs. |
| 1 | `Standard`, `DataZoneStandard` | Region geography. |
| 2 | `GlobalStandard` | `global`. |

Batch and unknown SKUs are excluded. Azure geography `Europe` maps to `eu`; `US` and `United States` map to `us`.
Other geography names use lowercase with spaces replaced by hyphens.
Healthy tier 0 deployments receive traffic first, then tier 1, then tier 2.
Selection uses capacity-weighted random choice within a tier. Weights never compare across tiers.
Degraded deployments rank after all healthy tiers. Five percent of initial attempts probe degraded deployments when available.
Disabled, open, throttled, and previously tried deployments are excluded.

Each request permits at most three attempts, with a different deployment on each attempt.
The proxy retries connect/DNS/TLS errors, 429, 500, 502, 503, and timeouts before response headers.
Other 4xx responses pass through without a retry.
A backend 401, 403, or 404 `DeploymentNotFound` disables that deployment until the next successful refresh.
Ordinary 404 responses pass through. The proxy inspects only a bounded JSON prefix for `DeploymentNotFound`.
Client disconnects abort processing without a health failure. Response body failures count as failures but never trigger retries.
Backend token acquisition failures return 503 without changing deployment health.

The replica retry budget uses a trailing ten-second window: `max(100, authenticatedRequests / 5)`, rounded down.
The floor represents ten retries per second across the window, without a separate one-second limiter.
Retries and unauthenticated requests do not increase the original request count.
When the budget is exhausted, the proxy returns the current backend error.

When only eligible untried throttled deployments remain, the proxy waits once if the shortest delay fits the deadline.
Otherwise it returns 429 with the shortest `Retry-After`. With no available or throttled candidate, it returns 503.
Clients should still handle terminal errors and rate limits.

### Deadlines

| Setting | Default |
| --- | --- |
| `RequestPipeline__OverallTimeout` | `00:02:00` (maximum `00:10:00`) |
| `RequestPipeline__NonStreamingTtfbTimeout` | unset |
| `RequestPipeline__StreamingTtfbTimeout` | `00:00:15` |

These settings use .NET TimeSpan values. All must be positive; the overall timeout cannot exceed 600 seconds.
A non-streaming response sends headers only after the whole completion.
So non-streaming requests have no separate TTFB timeout by default, and the overall deadline applies.
Set `NonStreamingTtfbTimeout` only if you want a shorter per-attempt limit for them.
A positive integer `x-lb-timeout-ms` can shorten the overall deadline. Invalid values return 400.
The overall deadline covers body buffering, token acquisition, retries, waits, and receipt of final backend response headers.
TTFB measures request sent through response headers. Timers stop when backend headers arrive.
Retries use only the time remaining before the original deadline.
Streams and ordinary error bodies have no total-duration timeout. A response body failure never retries after headers.

## Health and recovery

Health is in memory per replica and resets on process restart. It is not shared across replicas.
Retained deployment and account ARM IDs preserve health across refreshes, with case-insensitive comparison.
Removed deployments lose health. Accounts lose their circuit when no deployments remain.
Failed refreshes retain health. A removed and rediscovered deployment starts fresh.

| State | Routing effect | Recovery |
| --- | --- | --- |
| Healthy | Normal selection. | — |
| Throttled | Skipped. | Cooldown expires. |
| Degraded | Last choice, with 5% probe traffic. | TTFB remains below the recovery threshold for ten minutes. |
| Open | Skipped until one half-open probe is allowed. | A successful probe closes the circuit. |
| Disabled | Skipped. | Override removal, or successful refresh for backend misconfiguration. |

429 never counts as a circuit failure.
Throttle delay uses `retry-after-ms`, then `retry-after` seconds or HTTP date, then ten seconds.
The delay is clamped to 1–120 seconds. Concurrent responses retain the latest cooldown expiry.
Deployment circuits use a thirty-second sliding window.
They open after three consecutive failures, or at least five failures with a failure rate above 50%.
They allow one half-open probe after thirty seconds. Failed probes double the delay up to five minutes.
A successful probe resets the delay. Ignored outcomes release probes without changing the circuit.

Connect, DNS, or TLS errors open the whole account immediately.
An account also opens when more than half its deployment circuits are open.
Account recovery leaves individual deployment circuits to recover through their own probes.
Unchanged discovery refreshes retain recovered account circuits; membership changes recheck the majority condition.

The degradation detector uses nearest-rank TTFB p95 over five minutes, with at least twenty samples.
It records TTFB from streaming requests only, because non-streaming TTFB is the total generation time.
Non-streaming attempts still count as circuit successes and failures.
Peers use the same model name and version, and need twenty samples each.
Degradation requires p95 above twice the peer median and above the absolute floor.
Recovery requires ten minutes below 1.5 times the peer median.
Without qualified peers, the optional absolute threshold controls degradation; recovery uses 75% of that threshold.
Missing samples or threshold crossings restart recovery. Threshold changes retain samples.
Model changes clear latency samples and degradation, while preserving other health.
Evaluation occurs during health activity and snapshot reads.

## Operations

### Health endpoints

`GET /healthz` is anonymous and reports process liveness.
`GET /readyz` is anonymous and returns 503 until discovery first succeeds, then 200.
A successful empty table counts as ready. Later refresh failures retain readiness and the last successful snapshot.
Missing discovery configuration keeps readiness at 503.

`GET /admin/state` requires a valid caller key and returns routing and health for the responding replica.
Use it to inspect discovered deployments and their current health.
It does not expose caller keys or hashes. A request through a scaled Container App can reach any replica.
The response includes `replica`, `refreshedAt`, and deployments with routing fields and health.
Health includes state, account circuit status, attempt eligibility, throttle delay, and TTFB p95.
Responses use `Cache-Control: no-store`. Invalid keys return 401; no discovery snapshot returns 503.

`GET /admin/requests?limit=N` uses the same caller authentication and returns recent completed proxy requests, newest first.
Each replica retains 500 entries in memory. The default limit is 100; larger limits stop at 500.
Invalid or nonpositive limits return 400. Health and admin requests do not enter the feed.
Entries include an ID, UTC start time, caller, requested model, resolved model key, zone, and streaming flag.
They also include final HTTP status, outcome, total duration in milliseconds, and the backend attempt chain.
Request context: `operation` (path slug such as `chat.completions`, `responses`, `embeddings`, or `other`),
`apiVersion` (well-formed `api-version` query value), `requestBytes`, and `maxOutputTokens`.
`maxOutputTokens` comes from `max_completion_tokens`, then `max_tokens`, then `max_output_tokens`, integers only.
Attempts include deployment/account names, region, tier, backend status, TTFB in milliseconds, health outcome, and retry reason.
Attempts also include `backendRequestId` from `apim-request-id` or `x-request-id`, for Azure support cases.
Failed attempts with a JSON error body include `errorCode` (max 64 characters) and `errorMessage` (max 300 characters).
The LB reads at most the first 8 KiB of non-2xx JSON bodies for these fields. It never reads success bodies.
Backend status and TTFB are null when no response headers arrive. Terminal attempts have no retry reason.
`client_abort` and `failure` outcomes distinguish interrupted responses from ordinary HTTP errors.
Total duration includes response streaming; active streams appear after completion or disconnect.
Entries never contain request or response bodies, caller keys, backend tokens, or raw exceptions.
Oversized requested model identifiers retain only the first 512 characters followed by an ellipsis.
The buffer resets on restart. Requests through a scaled Container App can reach different buffers.

### Metrics, logs, and alerts

OpenTelemetry exports metrics and logs to Azure Monitor when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set.
Console logging remains available without an exporter destination.
Metrics include request count and outcome, TTFB, retries, state transitions, and throttle time.
Dimensions identify caller, model, deployment, region, and tier when available.
State-transition logs include account/deployment context. Logs never include caller credentials or request bodies.

The meter name is `openai_loadbalancer`:

| Metric | Kind | Meaning |
| --- | --- | --- |
| `lb.requests` | Counter | Requests by caller, model, deployment, region, tier, and outcome. |
| `lb.retries` | Counter | Backend retries. |
| `lb.ttfb` | Histogram, seconds | Request sent through backend response headers. |
| `lb.state_transitions` | Counter | Health changes, including previous/current state and misconfiguration. |
| `lb.throttle_time` | Histogram, seconds | Parsed and clamped cooldown for each backend 429, including extensions. |

`deployment` uses the full deployment ARM ID. Missing context uses `unknown`, `none`, or tier `-1`.
Health transitions use caller `unknown` because health belongs to the replica rather than one caller.
Throttle metrics carry the request's caller and measure assigned delay, rather than elapsed unique throttle time.
Without an Application Insights connection string, custom metric instruments remain available locally and console logs continue.

Slack alerts report Open, Degraded, backend misconfiguration, and recovery transitions.
Alert delivery runs outside the request path. Failed alert delivery logs a warning.
Configure `Operations__SlackWebhookUrl` through a secret reference, or use the Bicep `slackWebhookSecretName` parameter.

### Common checks

| Symptom | Check |
| --- | --- |
| `/readyz` returns 503 | Discovery scopes, file mounts, YAML validation, managed identity, and ARM Reader permissions. |
| Requests return 401 | Proxy key shape, caller hash, and whether the latest caller file refreshed successfully. |
| Requests return 400 | Model/version resolution, v1 body fields, and timeout header. |
| Requests return 403 | Caller zones and `x-lb-data-zone`. |
| Requests return 429 | Deployment throttle state, retry budget, and remaining deadline. |
| Requests return 503 | Available deployments, circuits, disabled overrides, and backend token acquisition. |
| Deployment becomes misconfigured | Cognitive Services OpenAI User assignment, backend deployment name, and backend authentication logs. |

## Deploy to Azure Container Apps

The Bicep deployment provisions Container Apps, a user-assigned identity, a container registry, monitoring, and sample OpenAI accounts.
It also provisions the dashboard Container App and its Entra app registration (see [Dashboard in Azure](#dashboard-in-azure)).
Prepare an existing RBAC-enabled Key Vault containing the two YAML documents as secret values.
The deployment grants its identity Key Vault Secrets User on that vault.
The deployment principal needs permission to create resources and assign roles in all configured scopes.
It also needs permission to create app registrations in the tenant.

Create valid local `callers.yaml` and `overrides.yaml` files using the formats above.
Publish them to your vault without printing secret values:

```sh
az keyvault secret set --vault-name <vault-name> --name lb-callers --file callers.yaml --output none
az keyvault secret set --vault-name <vault-name> --name lb-overrides --file overrides.yaml --output none
```

Set the vault resource ID before provisioning with Azure Developer CLI:

```sh
az login
azd auth login
azd env set CONFIG_KEY_VAULT_RESOURCE_ID /subscriptions/<subscription-id>/resourceGroups/<vault-resource-group>/providers/Microsoft.KeyVault/vaults/<vault-name>
azd up
```

The initial deployment uses placeholder containers until the source images are ready.
The post-provision hook builds the LB image (`src/Dockerfile`) and the dashboard image (`dashboard/Dockerfile`) in Azure Container Registry.
It then updates each Container App image and health probes together in one revision.
The update switches ingress from placeholder port 80 to application port 8080.
Use the `CONTAINER_APP_URL` deployment output as the client endpoint.
The application revision uses `/readyz` for readiness and `/healthz` for liveness.
The dashboard revision uses `/healthz` for both probes.
The placeholder revision omits those application probes.

| Bicep parameter | Default or purpose |
| --- | --- |
| `discoveryScopes` | Empty defaults to the generated resource group. Otherwise use supported subscription/resource-group scopes. |
| `keyVaultResourceId` | Required ARM ID of the existing configuration vault. |
| `callersFileSecretName` | `lb-callers`. |
| `overridesFileSecretName` | `lb-overrides`. |
| `slackWebhookSecretName` | Empty disables Slack; otherwise references a webhook secret in the same vault. |
| `imageName` | Optional prebuilt application image. When supplied, Bicep configures the application health probes immediately. |
| `dashboardImageName` | Optional prebuilt dashboard image. Same behavior as `imageName`. |

Set nondefault scope and secret-name parameters through Bicep deployment parameters.
For direct Bicep deployment, supply `imageName` and `dashboardImageName` with accessible images built from the current source.
With the default placeholder, run the post-provision hook to install the application image and probes.
The supplied `infra/main.parameters.json` maps `CONFIG_KEY_VAULT_RESOURCE_ID` and uses the default secret names.
It also accepts `OPENAI_CAPACITY` for sample deployment capacity.
Select available model names, versions, regions, and capacity through the OpenAI provisioning parameters in `infra/main.bicep`.
The historical model defaults may be unavailable in your regions; select supported versions before provisioning.
Set `openAiInstances` to `{}` and provide existing `discoveryScopes` to omit sample account creation.

Reader is assigned at each discovery scope's parent subscription to permit subscription location metadata reads.
This grants subscription-wide read visibility even when discovery lists only one resource group.
Cognitive Services OpenAI User is assigned at each configured discovery scope and inherited by its accounts.
The application only lists accounts inside its configured discovery scopes.

Container Apps references versionless Key Vault secret URLs and mounts the configuration files at:

- `/mnt/lb-config/callers.yaml`.
- `/mnt/lb-config/overrides.yaml`.

The templates configure the matching discovery file paths and scope environment variables.
Updating a Key Vault secret does not immediately update every replica.
Container Apps retrieves new versions of versionless references within thirty minutes; application refresh adds up to five minutes.
Confirm the mounted file and successful refresh before completing key rotation or relying on a manual drain.
See [Container Apps secret rotation](https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets#key-vault-secret-uri-and-secret-rotation).

Validate changed templates before deployment:

```sh
az bicep build --file infra/main.bicep
az bicep build --file infra/web.bicep
az bicep build --file infra/dashboard.bicep
```

Repeat the command for each changed Bicep template.
The former portal deployment and published legacy image use the removed static configuration; deploy the current source instead.

### Dashboard in Azure

`infra/dashboard.bicep` deploys the dashboard as a separate Container App with external ingress and exactly one replica.
The service merges replica batches in memory, so it must not scale out.
Its history (one hour per second, one day per minute) is in memory too, so a restart or new revision starts it empty.
It has its own user-assigned identity with only AcrPull.

The template creates a single-tenant app registration and its service principal through the Microsoft Graph Bicep extension (`infra/bicepconfig.json`).
Any user in the tenant can sign in; there is no group restriction.
The registration has no client secret. Easy Auth signs users in with ID tokens only.
It requests version 2 access tokens, so the audience is the application (client) ID.

Easy Auth redirects unauthenticated browser requests to Entra sign-in. `/healthz` stays anonymous.
It accepts bearer tokens from the dashboard app and from the LB managed identity.
The dashboard service then accepts `/ingest` only when the token's object ID matches the LB identity.

Bicep sets these values:

| Setting | App | Value |
| --- | --- | --- |
| `Dashboard__IngestUrl` | LB | `<dashboard URL>/ingest` |
| `Dashboard__Audience` | LB | Dashboard app registration client ID |
| `Dashboard__LbIdentityObjectId` | Dashboard | LB managed identity object ID |

Open the `DASHBOARD_URL` deployment output in a browser.
Build the dashboard image locally from the repository root with `docker build -f dashboard/Dockerfile .`.

## Development

Install the .NET 10 SDK. Build and test from the repository root:

```sh
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
dotnet build src/openai-loadbalancer.sln
dotnet test src/openai-loadbalancer.sln
```

Tests use fake ARM clients, controlled time, and isolated in-process backends.
They do not need Azure credentials or an external server.
Production uses managed identity; it does not fall back to a developer Azure CLI login.

### Local testing

Run the devkit and the LB from the repository root:

```sh
tools/devkit/run.sh
```

Open **http://localhost:5200** for the live routing dashboard. The LB listens on **http://localhost:5080**, avoiding the macOS AirPlay port.
The script builds the dashboard web app when it is missing or stale (requires [bun](https://bun.sh)), builds the solution, and starts the devkit, LB, and dashboard service.
Ctrl+C stops all three, including the fake account listeners.
The devkit control panel is at **http://localhost:5100**. It holds the traffic generator, failure presets, and account controls, and links to the dashboard.
Start traffic in the traffic panel. Choose a model, data zone, rate, concurrency, and streaming share.
Use a preset or account controls to simulate latency, throttling, missing deployments, and outages.
Deployment controls replace account controls; reset an override to inherit account controls again.
An outage stops the account listener. A deployment outage also stops its shared account listener.

Presets reset all controls before applying `sweden-slow`, `france-throttled`, `eastus2-outage`, or `eu-down`.
`recover-all` restores default controls. LB circuits retain their cooldown; recovery appears when the next probe succeeds.
The default eastus2 PTU deployment receives global chat traffic first. Apply `eastus2-outage` to watch retries reach other regions.
EU requests stay within EU deployments; global requests can use every region.

Mock ARM runs on port 5100; five fake Azure accounts run on ports 5101–5105.
`tools/devkit/config/scenario.yaml` defines accounts, model versions, SKUs, capacity, and geography.
The scenario includes regional chat, PTU, global chat, and embedding deployments.
The Development profile uses fake credentials and refreshes discovery every five seconds.
The devkit retains the plain local test key in its appsettings. `callers.yaml` stores only its SHA-256 hash.

The devkit binds to loopback and holds a public local test key. Keep it on your local machine.
Controls, presets, and deployment changes last until restart. Edit the scenario file to change startup defaults.
The control API is `GET/PUT /devkit/controls`; a PUT supplies `account`, optional `deployment`, and a complete `controls` object.
Set `controls` to null to reset defaults or remove a deployment override.
Error and throttle rates represent mutually exclusive request shares; their sum must not exceed one.
Use `POST /devkit/presets/{name}` and `GET/PUT /devkit/traffic` for presets and traffic.
Traffic settings are `requestsPerSecond`, `concurrency`, `model`, `streamingShare`, `zone`, and `enabled`.
Rates range from 0.1–100 requests/s; concurrency ranges from 1–100. In-flight streams finish when traffic stops.
Models with the `text-embedding-` prefix generate embedding requests; their streaming share has no effect.
Embedding vectors contain three simulated dimensions. Responses are for routing tests, not model accuracy tests.

Remove a deployment at runtime with `DELETE /devkit/deployments/{account}/{name}`.
Add or replace one with `PUT` at the same path and a JSON definition:

```json
{"name":"gpt4o-extra","model":"gpt-4o","version":"2024-11-20","sku":"Standard","capacity":60}
```

The next LB refresh sees the change. Removal differs from `missing`, which leaves ARM discovery intact and returns 404 at inference.
To start the LB alone, use `dotnet run --project src/openai-loadbalancer.csproj --launch-profile Development`.

### Live dashboard data

The separate `dashboard/server` service accepts LB batches at `POST /ingest`.
It serves an initial snapshot and one merged delta per second at `GET /api/stream`.
The snapshot carries request events received in the last two minutes. Replica state expires after thirty seconds without a batch.
Deployment state uses this precedence: Disabled, Open, Throttled, Degraded, Healthy.
Each deployment includes individual replica states and the largest replica p95 TTFB.
Each delta replaces merged state and rates. Its request array contains new sampled completions.
Deployment rates count backend attempts, including retries, with an outcome count for each deployment.
LB batches sample at most 200 request events; browser deltas sample at most 100 across all replicas.
Counts include attempts from unsampled requests. Slow browsers keep only the two latest queued deltas.
Route counts are not sampled either. A route is the caller, model, zone, final status, and attempt chain (deployment and outcome per attempt).
Each delta carries the route counts of its second in `routes`. The snapshot carries the last two minutes as `routeHistory`, one entry per second.
Older LB builds send no routes; the service accepts their batches unchanged.
Each frame numbers the live replicas in `replicaNumbers` ("Replica 1…N"). A new replica takes the lowest free number and keeps it while it sends batches.

The service keeps history in memory. A restart loses it; one replica holds all of it.

| Data | Retention | Hard cap (oldest out first) |
| --- | --- | --- |
| Route counts per second | 1 hour | 3,600 seconds, 200,000 route entries |
| Merged deployment state per second, stored on change | 1 hour | 100,000 changes |
| Per-minute route counts and worst state per deployment | 24 hours | 1,440 minutes, 400,000 route entries, 100,000 states |
| Sampled request events: every failure or retry, at most 4 others per second | 1 hour | 20,000 events and an estimated 32 MiB; others leave before failures and retries |
| Request events for the live snapshot, unthinned | 2 minutes | 20,000 events and an estimated 32 MiB |
| Latest record of each deployment seen | 24 hours | 5,000 deployments |
| Interned route shapes | while referenced | 10,000; further new shapes count under caller `(other)` |

Ingest rejects identifiers longer than 512 characters, so history never cuts one (an ARM deployment ID is about 170). At every cap, including 20,000 interned strings of up to 512 characters, history uses about 180 MB.
A typical day (71 deployments, 40 route entries/s, a few unhealthy deployments, 40 requests/s) uses about 30 MB, mostly request events.
At 40 requests/s with 2 % failures or retries, the hour holds about 17,300 events: all 2,880 notable ones and 4 others per second.

The snapshot does not carry the day in detail. Besides two minutes of requests and route ticks, it carries `summary`: one bucket per closed minute of the retained day with total, served, retried, failed, and refused requests, the worst deployment state, and the number of unhealthy deployments.
A delta whose tick closes a minute carries that one bucket in `summary`. `retention` gives the earliest instants with per-second and per-minute data.

`GET /api/history?from=<ISO>&to=<ISO>&resolution=<seconds>` returns detail for a past range, behind the same sign-in as the stream.
Resolutions are 1, 10, 30, 60, 300, 600, 1800, and 3600 seconds. The service returns 400 for an unparsable instant, another resolution, `from` not before `to`, or more than 1,500 buckets.
It clamps the range to retention. Resolutions below a minute use per-second data only; a minute or more uses per-minute data for a range that starts before the per-second hour.
Each bucket ends on a multiple of the resolution and holds route counts and, per unhealthy deployment, the worst state and its unhealthy seconds.
The response also holds up to 1,000 sampled requests (failures and retries first) and every deployment seen in the range, including removed ones.
JSON responses and static files are compressed with Brotli or gzip; the SSE stream is not.

Set `Dashboard__IngestUrl` on the LB to enable publishing. An empty value disables it.
Set `Dashboard__Audience` to the dashboard app registration's client ID (Bicep does this) or its Application ID URI.
The publisher requests the audience's `/.default` scope.
`Dashboard__Credential` defaults to `Azure__Credential`, and accepts `ManagedIdentity` or Development-only `Fake`.
The publisher holds at most 10,000 request events and drops the oldest on overflow.
The `lb.dashboard.dropped_requests` metric counts queue overflow in the `openai_loadbalancer.dashboard` meter.
Each push has a five-second timeout covering token acquisition and HTTP delivery, with no retry.
Only one push runs at a time. While it runs, publishing ticks wait for the next available slot.
Failed pushes discard their batch. The next push carries fresh state and newly queued events.

On the dashboard service, set `Dashboard__LbIdentityObjectId` to the LB managed identity's object ID.
Production requires Container Apps Easy Auth with a single-tenant Entra provider.
Easy Auth must validate bearer tokens and supply trusted `X-MS-CLIENT-PRINCIPAL` headers.
The service accepts ingest only with the configured identity's `oid`; browser routes require an Entra principal.
`/healthz` stays anonymous. [Dashboard in Azure](#dashboard-in-azure) describes the provisioned Easy Auth setup.
Development enables `Dashboard__DevAuth=true`, skips browser login, and requires the fake credential's static token on ingest.
DevAuth fails startup outside Development. This mode uses only simulated local credentials.

### Dashboard web app

`dashboard/web` is a TypeScript app (bun, Vite, React, Motion, PixiJS). `bun run build` writes it to `dashboard/server/wwwroot`, which is build output and not committed.
The page answers four questions, in this order.

- Is anything wrong, and where? The headline states it in words ("East US 2 is down"), followed on the same line by five figures: served, failed or refused, after a retry, spilled to a lower tier, and requests per second. The side panel lists each problem with its cause, timer, and effect on traffic. The browser tab title and icon show the verdict too. When the live stream stops for 5 s or more, the header says so.
- Where does traffic go? The scene is a funnel that reads left to right: callers, LB, region, deployment ("By region", the default). "By model" groups by model instead: callers, LB, model, region. Each group appears once; Global deployments form their own place. Under the LB, "Served by tier" gives the share each tier served and the share that spilled into it from a higher tier. Ribbon width is the share of requests that reached the node; the figure shows the share it served, in larger type for larger shares. Region labels sit in boxes that the ribbons enter and leave, so no text sits on a ribbon. Blue and red stripes in a ribbon are requests that left the node after a 429 or a failure. A deployment shows its share by weight when its actual share is clearly off it. The scene is built for 70 and more deployments:
  - Healthy deployments under 0.5 % of the window's requests fold into one lane per group, "12 quiet 0.4 %" (or "2 idle" when they took nothing). They unfold again above 1 %. Click the lane to list them; click again to fold them.
  - A deployment with a problem never folds and gets a taller label. Four or more deployments of one group with the same problem share one lane, such as "19 deployments down, Open, probe in 81 s" when an account is unreachable. A problem lane stays 15 s after the problem ends, so a deployment that flaps does not move every lane.
  - In the model view, models nobody uses gather in one "12 idle models" group.
  - Labels drop from three lines to two to one as lanes get shorter. A lane is never shorter than its label, so labels never overlap. When even one-line labels do not fit, the scene scrolls.
  - Requests refused by the LB end in their own red box, kept in view at the bottom of the scene.
- Why did requests fall back? "Fallbacks and failures" lists the attempt chains with exact counts ("France 429 → Germany served, 0.3 %"). Patterns with fewer than 3 requests and under 0.5 % fold into one line that expands on click. Click a chain to filter the request feed to it. The feed shows retries and failures by default, collapses consecutive requests with the same caller, status, and chain into one row with a count, and holds still while the pointer is over it.
- How does this compare with earlier? The time strip shows the last 2 min, 15 min, 1 h, or 24 h. It is a chart of requests per second with a scale, a legend, and a thin track that marks when any deployment had a problem; time with no retained data is hatched. The figures and the funnel cover a window that ends at the playhead: 10 s, 30 s, or 1 min in the 2-minute range; 1, 5, or 15 min; 5 min, 15 min, or 1 h; 1, 6, or 24 h in the longer ranges. In the 2-minute range, changes compare with the same window a minute earlier; in the longer ranges, with the window before. Click or drag the strip to look back. In the past, a blue "Not live" band over the verdict names the time shown, the stage gets a blue outline, and "Back to live" returns. Longer ranges fetch their detail from `/api/history` when chosen, and the figures never claim more time than the retained data covers.

All shares are exact: they come from unsampled route counts, never from the sampled particles.
Sampled requests travel their attempt chain as particles. A failed attempt bounces back to the LB in red, a 429 in blue. A streaming response flows back along its route until it ends.
Deployment states: Healthy (green ring), Throttled (blue ring that drains until the cooldown ends), Degraded (amber pulse), Open (dimmed, broken link), half-open (one probe dot in the gap), Disabled (grey, struck through). Each deployment also states its condition in text.
Hover a node to highlight its part of the funnel; for a deployment, also where its requests fell back to. Hover or focus a deployment for weight, tier, zone, p95, attempt rate, exact traffic shares with request counts, outcome mix, and per-replica states.
Names are human everywhere: regions by their Azure display name ("Poland Central"), replicas as "Replica 1…N" in the service's first-seen order, and two pools of one model as the model plus a suffix ("gpt-4o-mini · public"). Raw deployment, account, replica, and request IDs appear only as secondary text in hover cards and request detail.
Click a feed row for the request: operation, API version, body size, output limit, streaming, caller, and each attempt with its region, model, status, time to first byte, the backend's error code and message, and the Azure request ID for support. Rows with a failure show the error code and the start of the message. Hover a tier in "Served by tier" to highlight its deployments. Click a particle or a feed row for its attempt chain.
Filter by model, zone, and caller; filters, the window, and the feed mode stay in the URL. Pause, scrub, or replay the live two minutes; look back further with the time ranges. Space pauses, the arrow keys scrub, and Escape clears the selection.
The look follows the Recog brand of the ops portal: its shadcn colour tokens (neutral base, blue primary, success, warning, destructive), the Geist font, its radius scale, and the Recog mark in the toolbar and on the loading page. State colours map onto the brand: healthy is success, throttled the primary blue, slow the warning amber, failed the destructive red. Where a brand colour is too light or dark for WCAG AA as text, only its lightness changes; `theme.ts` notes each case, and a test keeps the stylesheet's colours equal to the canvas palette.
The page follows the system light or dark preference, has a theme toggle, and honours reduced motion: particles do not travel, results fade at their destination. On a phone, the funnel scrolls sideways.
The scene plays 2.5 seconds behind the server so that late batches still play in completion order.

Add `?demo=<scenario>` to run without a backend:
- the production inventory (71 deployments in 5 regions, about 21 models, skewed traffic from 4 illustrative callers): `production`, `production-sweden-slow`, `production-france-throttled`, `production-sweden-outage`, or `production-eu-down`;
- the devkit inventory: `calm`, `sweden-slow`, `france-throttled`, `eastus2-outage`, or `eu-down`.

The demo simulates the LB's selection, retries, and health rules in the browser. It also answers the history ranges with a generated day that includes past incidents.
Also `?view=model`, `?range=15m|1h|24h`, and `?theme=light|dark`.
For development, run `bun install` and `bun run dev` in `dashboard/web`. Vite proxies `/api` to `http://localhost:5200`, or to `DASHBOARD_URL`.
Checks: `bun run typecheck`, `bun run lint`, and `bun test`.

The fake credential fails startup outside Development. HTTP ARM endpoints also fail startup outside Development.
To use Azure during Development, override `Azure__Credential=ManagedIdentity` and `Discovery__ArmEndpoint=https://management.azure.com`.
Also override the discovery scopes and configuration file paths for that environment.

Build the container from the source directory:

```sh
docker build -t openai-aca-lb:local ./src
```

The Dockerfile uses .NET 10 SDK and ASP.NET runtime images. HTTP listens on port 8080.
Supply discovery configuration and both YAML files when deploying the image.
The former `BACKEND_*`, latency settings, and `HTTP_TIMEOUT_SECONDS` configuration are removed.
Use discovery, YAML overrides, and `RequestPipeline` settings instead.
