# Changelog

## Unreleased

### Added

- Show request counts next to every share in the dashboard funnel and top figures, the total request count in the figures and the LB column, and rates in the unit that keeps them readable ("0.5/min" instead of "0.0/s"). Long caller names widen the caller column instead of being cut off.
- Route a requested deployment name to the pool of every deployment with that name, across accounts and regions. Resolution order: model key, alias, deployment name, 400. Same-model deployments with different names stay separate pools.
- Map legacy names to a model key or a deployment name with `aliases` in the override file. An alias that equals a discovered model name fails the refresh.
- Report the pool kind and pool name in `/admin/requests` and dashboard routes.
- Configure the request body limit with `RequestPipeline__MaximumBodyBytes`.
- Record what each request was in `/admin/requests` and dashboard events: operation, API version, request bytes, requested output limit, and per attempt the backend request ID plus the Azure error code and message (bounded, from non-2xx JSON bodies only). The devkit fake accounts send Azure-style error bodies and `apim-request-id`.
- Keep dashboard history in memory with hard caps: per-second route counts and states for an hour, per-minute aggregates for a day, and up to 20,000 request events for an hour. The snapshot adds a per-minute day `summary`, `retention`, and stable `replicaNumbers`; `GET /api/history` returns detail for a past range, compressed.

- Add an isolated LB dashboard publisher, authenticated ingest service, replica merge, and live SSE placeholder on port 5200.
- Retain two minutes of dashboard request events, expire stale replicas, and publish unsampled deployment attempt counts.
- Scale the dashboard to the production inventory (71 deployments): a region or model view, quiet and same-problem deployments folded into one lane each, label density that follows lane height so labels never overlap, time ranges of 2 min to 24 h with a not-live band, human names for regions, replicas and pools, request context and backend errors in the request detail, a `?demo=production` scenario set, and the Recog brand (tokens, Geist, mark, loading page).
- Add the animated dashboard web app: request particles, deployment state rings, hover detail, request feed, filters, pause and scrub, light and dark themes, reduced motion, and an in-browser demo mode.
- Publish each deployment's throttle deadline, circuit open deadline, and half-open flag to the dashboard.
- Publish unsampled route counts (caller, model, zone, final status, attempt chain) from the LB, and stream them per second with two minutes of history in the dashboard snapshot.
- Redesign the dashboard around operator questions: a headline verdict with impact, a caller → LB → tier → region → deployment funnel with exact traffic shares, spill and fallback, a problems list, fallback paths with counts, changes against a minute earlier, and a feed that shows retries and failures by default.
- Rework the dashboard funnel region first (callers → LB → region → deployment, with a per-tier split and spill at the LB), put region labels in boxes clear of ribbons, compact idle fallbacks, and size shares by importance. Put the verdict and five figures on one line, turn the time strip into a chart with scale, legend, and readout, group identical requests in the feed, fold rare fallback paths, add usage hints, a stale-stream warning, and the verdict in the tab title, and raise light-mode text to WCAG AA.
- Package the dashboard as its own image and Container App: one replica, single-tenant Easy Auth, a Graph-provisioned app registration, and LB ingest settings wired in Bicep. The post-provision hook builds and deploys both images.

- Add a local devkit with mock ARM, fake OpenAI accounts, failure controls, traffic generation, and a routing dashboard.

- Configure the ARM origin and discovery refresh interval, with HTTP ARM restricted to Development.
- Select a shared Development-only fake Azure credential and a devkit launch profile.
- Expose authenticated `/admin/requests` with 500 completed requests per replica and backend attempt metadata.
- Discover Azure OpenAI deployments and capacity every five minutes through managed identity.
- Route by model/version, SKU tier, capacity weight, health, and caller data zone.
- Configure version defaults, exclusions, manual drains, and model health thresholds through YAML.
- Authenticate named callers with rotating proxy keys stored as SHA-256 hashes.
- Track deployment/account circuits, throttling, TTFB degradation, and replica-local retry budgets.
- Export OpenTelemetry metrics and logs to Azure Monitor; send Slack alerts for health changes and recovery.
- Expose authenticated `/admin/state` and anonymous `/healthz` and `/readyz` endpoints.
- Mount Key Vault-backed caller and override files in Container Apps and provision discovery/backend role assignments.
- Add unit and in-process integration tests for discovery, health, authentication, forwarding, streaming, and operations.

### Changed

- `/admin/requests` returns only the authenticated caller's requests; the limit applies after that filter.
- An alias may equal the model name of an excluded, unsuccessful, or unsupported deployment; only routable deployments block it.
- Move the LB Development listener to port 5080 to avoid macOS AirPlay.
- Trim the devkit page to traffic, presets, and account controls, with a link to the dashboard. Remove its monitoring views and the `/devkit/state` and `/devkit/requests` proxy endpoints.
- Move the shared request and dashboard wire records into `src/Contracts`, so the dashboard no longer references the LB executable.

- Upgrade the application and container images to .NET 10, with current Azure.Identity and YARP packages.
- Limit each request to three distinct deployments and enforce deadlines through backend response headers.
- Buffer request bodies up to 26 MiB by default (was 16 MiB) for retries, so 25 MB audio uploads fit, while streaming backend responses without total-duration timeouts.
- Remove the default 30-second TTFB timeout for non-streaming requests; only the overall deadline applies unless `NonStreamingTtfbTimeout` is set.
- Allow `RequestPipeline__OverallTimeout` up to 600 seconds (default still 120 seconds).
- Record degradation TTFB from streaming requests only. Non-streaming latency no longer marks deployments degraded.
- Consolidate discovery, health, and request documentation into the README operator guide.

### Fixed

- Skip a malformed ARM deployment (no valid name, model, version, or capacity) with a warning instead of failing the whole discovery refresh.
- Run the post-provision hook under POSIX `sh`, as `azure.yaml` declares: replace the bash-only placeholder substitution with `sed`.
- Keep each deployment's TTFB samples sorted, so health reads the p95 by index instead of sorting the five-minute window on every snapshot and attempt.
- Count only authenticated requests in the retry budget, so rejected caller keys cannot increase permitted retries.

### Breaking changes

- Remove static `BACKEND_*` configuration, backend API keys, legacy latency settings, and `HTTP_TIMEOUT_SECONDS`.
- Require discovery scopes, caller/override file paths, and a managed identity with Reader and OpenAI User permissions.
- Require proxy-issued caller keys. Azure OpenAI keys and arbitrary client keys no longer authenticate to the proxy.
- Resolve client models by name and optional version rather than using configured backend deployment aliases. Deployment names in the path still work and route to the pool of deployments with that name.
