# Load balancing design

Status: implemented. Replaces the static `BACKEND_n_*` model.

## Goals

- Route each request to a deployment of the requested model, across regions.
- Split traffic by quota. Fall back between regions and tiers on throttling, errors, slowness and outages.
- Respect data residency.
- Discover deployments and quota automatically; override by exception.

Non-goals: fallback to a different model (the orchestrator does that), shared state across replicas.

## 1. Discovery

Every 5 minutes, using the proxy's managed identity (Reader on the configured scopes):

1. List `Microsoft.CognitiveServices/accounts` of kind `OpenAI` / `AIServices` in the configured subscriptions or resource groups. Take `properties.endpoint` and `location`.
2. List `.../accounts/{name}/deployments`. Keep only `provisioningState == Succeeded`. Read:
   - `properties.model.name`, `properties.model.version` → model key
   - `sku.name` → tier and data zone
   - `sku.capacity` → weight
3. Map each region to a geography with `GET /subscriptions/{id}/locations` (`metadata.geographyGroup`), overridable.
4. Apply the override file. Swap in the new routing table atomically. Log every add, remove and capacity change.

If a refresh fails, keep the last good table and log an error. If the first refresh fails, the replica reports not ready.

Health state is keyed by deployment and survives refreshes for deployments that still exist.

## 2. Routing table

**Model key** = name + version. Clients address a model in the deployment path segment, or in the `model` body field on the v1 API:

- `gpt-4o@2024-11-20` → that version only.
- `gpt-4o` → the default version from the override file. If none is set and only one version exists, use it. Otherwise return 400 listing the versions.
- An alias from the override file → its target (a model key or a deployment name). Aliases match case-insensitively and must not equal a discovered model name (the refresh fails with a config error).
- A discovered deployment name (for example `llm-gpt-4o`) → the pool of all deployments with exactly that name, across accounts. This keeps today's paths working with no config, and keeps separately named deployments of one model (`llm-gpt-4omini` vs `llm-gpt-4omini-public`) as separate pools. If one name maps to different model keys, route anyway and log a warning at refresh.
- Resolution order: model key → alias → deployment name → 400. Health, weights and degradation peers apply per pool.
- Request body limit: 26 MB by default (Whisper accepts 25 MB), configurable. Multipart bodies pass through verbatim.

**Tier**, derived from `sku.name`:

| Tier | SKU |
|---|---|
| 0 | `ProvisionedManaged`, `DataZoneProvisionedManaged`, `GlobalProvisionedManaged` |
| 1 | `Standard`, `DataZoneStandard` |
| 2 | `GlobalStandard` |
| excluded | `*Batch`, anything unknown |

**Weight** = `sku.capacity`. Units are PTU in tier 0 and K TPM in tiers 1–2, so weights only compare within a tier.

**Data zone**:

- `Standard`, `ProvisionedManaged`, `DataZone*`: the region's geography (`eu`, `us`, …).
- `Global*`: `global`.

## 3. Overrides

One YAML file, reloaded on each refresh. It applies to all replicas, so it is also the manual control surface.

```yaml
defaultVersions:
  gpt-4o: "2024-11-20"
aliases:                                     # legacy deployment names → model key
  chat: gpt-4o@2024-11-20
  embedding: text-embedding-3-large@1
exclude:
  - account: oai-legacy-westeurope           # whole account
  - account: oai-swedencentral
    deployment: gpt4o-test                   # one deployment
deployments:
  - account: oai-francecentral
    deployment: gpt4o
    tier: 1                                  # pin tier
    weightMultiplier: 0.5
    disabled: true                           # manual drain
regions:
  switzerlandnorth: { zone: eu }
```

## 4. Request constraints

- Each caller key lists its allowed zones, in order (section 10). Example: `[eu, global]`.
- Header `x-lb-data-zone: eu | us | global` selects one of them. No header → the first zone in the list. A zone not in the list → 403.
- `global` allows every zone. Any other zone allows only deployments with that zone. Tier does not override this.

## 5. Deployment state

| State | Enter | Effect | Exit |
|---|---|---|---|
| Healthy | default | normal | — |
| Throttled | 429 | skipped | `retry-after-ms`, else `retry-after` (seconds or HTTP date), else 10 s. Clamp to [1 s, 120 s]. |
| Degraded | see below | ranked last (section 6) | see below |
| Open | circuit breaker trips | skipped | half-open probe succeeds |
| Disabled | override file | skipped | override file |

429 never counts as a failure.

**Circuit breaker**, per deployment, 30 s sliding window. Trip when either:

- 5 or more failures and failure rate > 50%, or
- 3 consecutive failures.

Open for 30 s, doubling on each failed probe, max 5 min. Half-open allows one request at a time. One success closes the circuit and resets the backoff.

**Resource-level circuit.** Connect, DNS and TLS errors open the whole account at once. The account also opens when more than half of its deployments are Open. It uses the same half-open logic, then each deployment recovers on its own.

**Degraded.** Measure time to first byte (TTFB) on **streaming** requests only: request sent → response headers received. A non-streaming response sends headers only after the whole completion, so its "TTFB" is total generation time and is not recorded. Do not measure total duration, because it depends on output length and client speed. Keep a p95 per deployment over a 5-minute sliding window, with at least 20 samples. A deployment becomes Degraded when both are true:

- its p95 > 2× the median p95 of the other deployments of the same model key, and
- its p95 > an absolute floor (default 2 s, per-model override).

A deployment with no peers uses only an optional per-model absolute threshold. It exits after 10 minutes with p95 < 1.5× the peer median (hysteresis). Degraded deployments get 5% probe traffic, so recovery can be observed.

Known limitation: TTFB grows with prompt size. A relative signal limits this, because peers see the same traffic mix.

## 6. Selection

```
candidates = deployments of model key
           ∩ allowed data zone
           − Disabled, Open, Throttled, already tried in this request

order:  tier 0 healthy → tier 1 healthy → tier 2 healthy → degraded (any tier, by tier)
pick:   first non-empty group, weighted random by weight
probe:  5% of requests pick a degraded deployment first, if one exists
none:   429 with the shortest remaining Retry-After if any candidate is Throttled, else 503
```

## 7. Error handling

| Outcome | Retry | Health effect |
|---|---|---|
| Connect / DNS / TLS failure | yes, other account | failure (account level) |
| 429 | yes | Throttled |
| 500, 502, 503 | yes | failure |
| Timeout before first byte | yes | failure |
| Failure after first byte | no, response already streaming | failure |
| 404 `DeploymentNotFound`, 401/403 to the proxy | yes | deployment Disabled until next refresh, alert |
| Other 4xx (400, 413, content filter, …) | no | none |
| Client disconnect | no, abort | none |

## 8. Retry budget

- Max 3 attempts per request. Never the same deployment twice.
- Overall deadline: default 120 s, configurable up to 600 s. A client can shorten it with `x-lb-timeout-ms`.
- Per-attempt TTFB timeout, streaming: 15 s. Non-streaming: no separate per-attempt timeout by default (the overall deadline applies), because headers arrive only after the full completion; optionally configurable. No total-duration timeout on streams.
- Global retry budget per replica: retries ≤ 20% of requests over 10 s, with a floor of 10 retries/s. Over budget, return the error and do not retry.
- All candidates Throttled: if the shortest Retry-After fits in the remaining deadline, wait once and retry. Otherwise return 429 with that Retry-After.
- Buffer the request body for retries. Limit 26 MB by default (configurable), else 413.

## 9. Backend auth

The proxy always calls backends with its managed identity. One cached `TokenCredential`, scope `https://cognitiveservices.azure.com/.default`. No API keys, no client-token forwarding. The identity needs `Cognitive Services OpenAI User` on each discovered account. A 401/403 marks the deployment as misconfigured (section 7).

## 10. Inbound auth

Callers authenticate with an API key issued by the proxy, so any OpenAI-compatible framework works. These keys are not Azure OpenAI keys.

- Accept the key in `api-key: <key>` (Azure SDK style) or `Authorization: Bearer <key>` (OpenAI SDK style, v1 API).
- Strip both headers before forwarding. The backend sees only the managed identity token (section 9).
- Missing or unknown key → 401. `/healthz` and `/readyz` stay anonymous.

Keys belong to named callers:

```yaml
callers:
  - name: orchestrator
    zones: [eu, global]                             # first = default (section 4)
    keyHashes: ["sha256:9f2c…", "sha256:41ab…"]     # several valid keys allow rotation
```

- Keys have the prefix `lbk_` and 32 random bytes, base64url encoded, so secret scanners can find them.
- The config holds only SHA-256 hashes. Compare in constant time.
- The callers file is stored in Key Vault and mounted as a Container Apps secret. It reloads with the override file (section 3).
- Rotation: add the new hash, update the caller, remove the old hash.
- Logs and metrics carry the caller name, never the key.

Later: accept Entra ID tokens next to keys, for callers that support them.

## 11. Observability

- Response headers: `x-lb-deployment`, `x-lb-region`, `x-lb-attempts`.
- OpenTelemetry metrics by caller, model, deployment, region and tier:
  - request count and outcome
  - TTFB histogram
  - retries
  - state transitions
  - throttle time
- A log line on every state transition. Slack alerts on Open, Degraded and misconfigured deployments, and on recovery.
- `GET /admin/state` (authenticated, per replica): routing table and health.

## 12. Implementation phases

Each phase must build, and its tests must pass, before the next phase starts. Put pure logic behind interfaces, and inject `TimeProvider` so tests control time.

1. **Foundation and routing table.**
   - Upgrade to `net10.0` and current YARP / Azure.Identity. Remove unused packages.
   - Add an xUnit test project and a solution that contains both.
   - Add pure domain types and logic:
     - deployment records
     - SKU → tier / zone mapping
     - region → geography mapping input
     - model key resolution (`name`, `name@version`, default versions)
     - override application
     - routing table build
   - Add parsing of the YAML override and callers files (YamlDotNet).
   - No I/O and no request path changes. Old code stays in place.
2. **Discovery.**
   - Add an ARM client behind an interface: accounts, deployments, locations.
   - Add a background refresh service with an atomic table swap, last-known-good on failure, and change logging.
   - Add `/readyz`: not ready until the first successful refresh.
   - Reload the override and callers files on each refresh.
   - Config: discovery scopes (subscription IDs or resource-group IDs) and file paths.
   - Test with a fake ARM client.
3. **Health state.**
   - Throttled state, with Retry-After parsing and clamping.
   - Circuit breaker for deployments and accounts, with half-open probes and backoff.
   - Degraded detector: relative TTFB p95, floor, hysteresis.
   - Global retry budget.
   - All of it in memory per replica and unit tested with a fake clock.
4. **Request pipeline.** Replace the old path completely:
   - inbound key auth and zone resolution (section 10, section 4)
   - model resolution from the path or the v1 body
   - selection (section 6)
   - forwarding with YARP `IHttpForwarder` to the chosen deployment
   - segment-wise path rewrite and a cached managed identity token (section 9)
   - error classification and retries (sections 7–8)
   - `x-lb-*` response headers

   Delete `RetryMiddleware`, `YarpConfiguration`, `BackendConfig`, `LatencyTracker` and `LatencyConfig`. Add integration tests with in-process fake backends: streaming, 429 fallback, 5xx, timeouts, client abort, residency, auth.
5. **Operations.**
   - OpenTelemetry metrics and logs with the Azure Monitor exporter (section 11).
   - Slack alerts on state transitions.
   - `GET /admin/state`.
   - Bicep: remove the `BACKEND_*` params; add the discovery scope, Reader + `Cognitive Services OpenAI User` role assignments, and Key Vault–backed secret mounts for the callers and override files.
   - Update the README and CHANGELOG.

## 13. Later

- Weight × live capacity factor from `x-ratelimit-remaining-tokens`.
- Shared state across replicas (Redis), if per-replica convergence turns out too slow.
