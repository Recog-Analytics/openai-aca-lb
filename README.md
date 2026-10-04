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
| `AZURE_CLIENT_ID` | User-assigned managed identity client ID. Omit for a system-assigned identity. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Azure Monitor export destination. |
| `Operations__SlackWebhookUrl` | Optional HTTPS Slack webhook. An empty value disables alerts. |

Supply at least one scope and both file paths. Relative paths resolve against the application content root.
Both files must exist. Empty files are valid; an empty callers file grants no access.
Scopes and file paths are startup settings. File contents reload at startup and every five minutes.

Discovery reads `OpenAI` and `AIServices` accounts through the public Azure management endpoint.
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

Use the override file for version defaults, exclusions, manual drains, weight adjustments, and region corrections:

```yaml
defaultVersions:
  gpt-4o: "2024-11-20"
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

`degradedFloorSeconds` defaults to 2. The optional `degradedThresholdSeconds` applies when no peer has enough TTFB samples.
Both settings require finite, positive seconds. `modelHealth` keys must include a version.

## Client requests

Address models by their model name, optionally followed by `@version`:

- Azure API: `/openai/deployments/gpt-4o@2024-11-20/chat/completions?api-version=...`.
- OpenAI v1 API: `/openai/v1/chat/completions` or `/v1/chat/completions`, with `"model": "gpt-4o@2024-11-20"`.

An unversioned model uses the configured default, or the only discovered version.
An unknown model, unavailable default, or ambiguous version returns 400 with available versions.
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
Use the requested model name rather than a backend deployment name.
Responses include `x-lb-deployment`, `x-lb-region`, and `x-lb-attempts` for the selected backend and attempt count.

The proxy buffers request bodies up to 16 MiB for retries. Larger bodies return 413.
Azure-style request bodies are preserved verbatim. SSE responses stream as chunks arrive without response buffering.
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
| `RequestPipeline__OverallTimeout` | `00:02:00` |
| `RequestPipeline__NonStreamingTtfbTimeout` | `00:00:30` |
| `RequestPipeline__StreamingTtfbTimeout` | `00:00:15` |

These settings use .NET TimeSpan values. All must be positive; the overall timeout cannot exceed 120 seconds.
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
Prepare an existing RBAC-enabled Key Vault containing the two YAML documents as secret values.
The deployment grants its identity Key Vault Secrets User on that vault.
The deployment principal needs permission to create resources and assign roles in all configured scopes.

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

The initial deployment uses a placeholder container until the source image is ready.
The post-provision hook builds the current source in Azure Container Registry.
It then updates the Container App image and health probes together in one revision.
The update switches ingress from placeholder port 80 to application port 8080.
Use the `CONTAINER_APP_URL` deployment output as the client endpoint.
The application revision uses `/readyz` for readiness and `/healthz` for liveness.
The placeholder revision omits those application probes.

| Bicep parameter | Default or purpose |
| --- | --- |
| `discoveryScopes` | Empty defaults to the generated resource group. Otherwise use supported subscription/resource-group scopes. |
| `keyVaultResourceId` | Required ARM ID of the existing configuration vault. |
| `callersFileSecretName` | `lb-callers`. |
| `overridesFileSecretName` | `lb-overrides`. |
| `slackWebhookSecretName` | Empty disables Slack; otherwise references a webhook secret in the same vault. |
| `imageName` | Optional prebuilt application image. When supplied, Bicep configures the application health probes immediately. |

Set nondefault scope and secret-name parameters through Bicep deployment parameters.
For direct Bicep deployment, supply `imageName` with an accessible image built from the current source.
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
```

Repeat the command for each changed Bicep template.
The former portal deployment and published legacy image use the removed static configuration; deploy the current source instead.

## Development

Install the .NET 10 SDK. Build and test from the repository root:

```sh
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
dotnet build src/openai-loadbalancer.sln
dotnet test src/openai-loadbalancer.sln
```

Tests use fake ARM clients, controlled time, and isolated in-process backends.
They do not need Azure credentials or an external server.
The runtime uses managed identity; it does not fall back to a developer Azure CLI login.

Build the container from the source directory:

```sh
docker build -t openai-aca-lb:local ./src
```

The Dockerfile uses .NET 10 SDK and ASP.NET runtime images. HTTP listens on port 8080.
Supply discovery configuration and both YAML files when deploying the image.
The former `BACKEND_*`, latency settings, and `HTTP_TIMEOUT_SECONDS` configuration are removed.
Use discovery, YAML overrides, and `RequestPipeline` settings instead.
