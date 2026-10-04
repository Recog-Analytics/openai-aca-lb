# Changelog

## Unreleased

### Added

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

- Upgrade the application and container images to .NET 10, with current Azure.Identity and YARP packages.
- Limit each request to three distinct deployments and enforce deadlines through backend response headers.
- Buffer request bodies up to 16 MiB for retries while streaming backend responses without total-duration timeouts.
- Consolidate discovery, health, and request documentation into the README operator guide.

### Fixed

- Count only authenticated requests in the retry budget, so rejected caller keys cannot increase permitted retries.

### Breaking changes

- Remove static `BACKEND_*` configuration, backend API keys, legacy latency settings, and `HTTP_TIMEOUT_SECONDS`.
- Require discovery scopes, caller/override file paths, and a managed identity with Reader and OpenAI User permissions.
- Require proxy-issued caller keys. Azure OpenAI keys and arbitrary client keys no longer authenticate to the proxy.
- Resolve client models by name and optional version rather than using configured backend deployment aliases.
