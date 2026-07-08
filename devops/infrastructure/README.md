# devops/infrastructure

Bicep infrastructure-as-code lands here with the roadmap M2 work (#30 deployed
Orleans, #49 Docker/ACA). Conventions for that work:

- `bicepconfig.json` lives in this folder (not repo root); all analyzer rules
  enforced as `error`; `resourceTypedParamsAndOutputs` enabled.
- Per-service layout: `{service}/resources/root.bicep` (subscription-scope entry
  point, params + delegation only), `{service}/resources/main.bicep` (owns naming
  and orchestration), `{service}/resources/modules/` (one module per resource
  type), `{service}/parameters/root.{env}.bicepparam`.
- Shared helpers (`functions.bicep`) sit at this folder's root.
- Managed identity everywhere; no connection strings; no hardcoded names or
  locations; `.bicepparam` files are compiled to JSON artifacts in the build
  pipeline, never passed raw to deployments.
