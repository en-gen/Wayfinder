# devops/infrastructure

## Azurite (local dev)

`docker-compose.yml` runs [Azurite](https://github.com/Azure/Azurite) - the
Azure Storage emulator - for local development. Flow.Silo's journaled-grain
storage (work item #54) is Azure Blob-backed, and Azurite gives every
developer a local blob endpoint without an Azure subscription.

```
docker compose -f devops/infrastructure/docker-compose.yml up -d
```

This starts blob (10000), queue (10001), and table (10002) endpoints backed by
a named volume, so data survives container restarts. Flow.Silo's `Development`
configuration (`appsettings.Development.json`) already points its `Azure:Storage`
section at Azurite's well-known development connection string:

```json
"Azure": {
  "Storage": {
    "connectionString": "UseDevelopmentStorage=true",
    "CaseStateContainer": "case-flow-casestate"
  }
}
```

`UseDevelopmentStorage=true` resolves to account `devstoreaccount1` with
Azurite's fixed, publicly documented well-known key - not a secret, safe to
commit. No setup beyond `docker compose up -d` is required before running the
silo locally.

The silo registers its `BlobServiceClient` from that section's *shape*
(Microsoft.Extensions.Azure): a `connectionString` key selects a
connection-string client. The deployed flip (work item #30) is config-only -
same section, endpoint + managed identity instead, no code change:

```json
"Azure": {
  "Storage": {
    "serviceUri": "https://<account>.blob.core.windows.net",
    "credential": "managedidentity",
    "CaseStateContainer": "case-flow-casestate"
  }
}
```

(add `"clientId"` for a user-assigned identity). `appsettings.json` ships the
section empty as a deliberate fail-fast placeholder until #30 fills it per
environment. See `Flow.Silo/Infrastructure/Options/AzureOptions.cs` for the
full key contract.

To stop the stack: `docker compose -f devops/infrastructure/docker-compose.yml down`
(add `-v` to also drop the data volume).

## Bicep (reserved)

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
