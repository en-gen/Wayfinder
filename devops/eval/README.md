# devops/eval

A real **multi-container** Orleans cluster for Case.Flow (work item #49): Azurite plus 3
separate `Flow.Silo` containers running the DEPLOYED clustering path
(`Program.cs` `ConfigureDeployedOrleans` - real Azure Table clustering + reminders,
container-aware endpoints, work item #30) against Azurite instead of Orleans TestingHost's
in-process `TestCluster` membership.

This is a **new, separate stack** from `devops/infrastructure/docker-compose.yml` (the F5
Azurite-only stack backing `dotnet run`'s `Development` configuration) - that file is
untouched. This stack proves out the *deployed* configuration as 3 independent OS processes
that have to find each other over the network, not just compile.

## What's in the stack

- `azurite` - the Azure Storage emulator, blob/queue/table endpoints, healthchecked.
- `silo1`, `silo2`, `silo3` - three containers built from `src/Flow.Silo/Dockerfile`, each
  running with `DOTNET_ENVIRONMENT=Docker` / `ASPNETCORE_ENVIRONMENT=Docker` (anything other
  than `Development`, so `Program.cs`'s `ConfigureOrleans` takes the `ConfigureDeployedOrleans`
  branch), each pointed at Azurite via **explicit connection strings** (see the gotcha below),
  each publishing its co-hosted web port on a distinct host port so you can curl all three
  independently.

## The container-networking gotcha this stack proves out

`UseDevelopmentStorage=true` (the shortcut `appsettings.Development.json` uses for local
`dotnet run`) resolves Azurite to **127.0.0.1** - correct only when the silo and Azurite share
a network namespace (a single dev box). Separate silo **containers** need to reach Azurite by
its **compose service hostname** instead. So `docker-compose.yml` sets explicit connection
strings via `Azure__Clustering__connectionString` / `Azure__Storage__connectionString`,
pointed at `azurite` (the compose service name, resolved through Docker's embedded DNS):

```
DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://azurite:10000/devstoreaccount1;QueueEndpoint=http://azurite:10001/devstoreaccount1;TableEndpoint=http://azurite:10002/devstoreaccount1;
```

That account name/key are Azurite's fixed, publicly documented well-known development
credentials - not a secret, safe to commit.

`Azure__Storage__CaseStateContainer=case-flow-casestate` is set alongside it (same container
name the `Development` config uses).

## Advertised address

Each silo advertises the address other silos dial it on (`Program.cs`
`ResolveAdvertisedIPAddress`): an explicit `Orleans:AdvertisedIPAddress` /
`ORLEANS__ADVERTISEDIPADDRESS` if set, otherwise the first non-loopback IPv4 address the
container's own hostname resolves to. This stack does **not** set
`ORLEANS__ADVERTISEDIPADDRESS` - each container's own hostname (`silo1`/`silo2`/`silo3`, set
explicitly via `hostname:` in the compose file for readability) resolves via Docker's
container-local `/etc/hosts` entry to that container's own address on the compose network,
which is exactly what the fallback needs and what other containers can dial. If a future
bring-up on a different Docker networking setup shows silos failing to find each other, set
`ORLEANS__ADVERTISEDIPADDRESS` explicitly per service in the compose file (each container's own
address on the compose network) and document why here.

## Bring up

```
docker compose -f devops/eval/docker-compose.yml up -d --build
```

Give the cluster a few seconds after the containers report `Up`/`healthy` - real Azure Table
membership needs the silos to gossip/reconcile through the table before every silo reliably
reports as Active (unlike Orleans TestingHost's in-process membership, this isn't
instantaneous).

## Verify the cluster formed

Curl the cluster-status endpoint (`GET /cluster`, also served at `GET /`; see
`src/Flow.Silo/Startup.cs`) on **each** silo - all three should agree the cluster has 3 active
silos, proof the 3 separate CONTAINERS discovered each other through real Azure Table
membership in Azurite, not just that each one started in isolation:

```
curl http://localhost:8081/cluster
curl http://localhost:8082/cluster
curl http://localhost:8083/cluster
```

Expected shape:

```json
{ "activeSilos": 3, "silos": [ { "address": "...", "status": "Active" }, ... ] }
```

While the cluster is still converging (or if a silo's client hasn't connected to a gateway
yet), the same endpoint returns 200 with `"activeSilos": 0` and an `"error"` field describing
why, rather than a 500 - safe to poll during bring-up.

`GET /health` on any silo is a bare liveness probe (200 once Kestrel is serving requests at
all) - it does not depend on cluster membership. It also backs each container's own
`HEALTHCHECK` (see `src/Flow.Silo/Dockerfile`).

## Tear down

```
docker compose -f devops/eval/docker-compose.yml down -v
```

(`-v` also drops the `azurite-eval-data` volume, so the next bring-up starts from a clean
membership table.)

## Notes

- Azurite publishes **no host ports** - the silos reach it entirely over the internal Docker
  network via the `azurite` service hostname (container-internal ports 10000/10001/10002). This
  keeps the eval stack from colliding with any other Azurite a developer runs for other projects;
  no host-side tool (e.g. Storage Explorer) is expected to connect. (The silo containers still
  publish their web ports 8081-8083 so you can `curl` the cluster-status endpoint from the host.)
- Out of scope for this stack (separate work items): the case-operation REST API (create/drive
  a case - #32) and the Quartz/timer redesign (#31).
