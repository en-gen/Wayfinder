# devops/eval

A real **multi-container** Orleans cluster for Case.Flow (work item #49), extended by work item
#32/#33 sub-unit 4 ("First Light") with a real authenticated case-operation API surface: Azurite
plus 3 separate `Flow.Silo` containers running the DEPLOYED clustering path (`Program.cs`
`ConfigureDeployedOrleans` - real Azure Table clustering + reminders, container-aware endpoints,
work item #30) against Azurite instead of Orleans TestingHost's in-process `TestCluster`
membership, plus Postgres + Zitadel so the same stack can demonstrate `/api/v1/...` (work item
#32/#33) against a real OIDC token instead of a faked one.

This is a **new, separate stack** from `devops/infrastructure/docker-compose.yml` (the F5
Azurite-only stack backing `dotnet run`'s `Development` configuration) - that file is
untouched. This stack proves out the *deployed* configuration as independent OS processes
that have to find each other over the network, not just compile.

## What's in the stack

- `azurite` - the Azure Storage emulator, blob/queue/table endpoints, healthchecked.
- `silo1`, `silo2`, `silo3` - three containers built from `src/Flow.Silo/Dockerfile`, each
  running with `DOTNET_ENVIRONMENT=Docker` / `ASPNETCORE_ENVIRONMENT=Docker` (anything other
  than `Development`, so `Program.cs`'s `ConfigureOrleans` takes the `ConfigureDeployedOrleans`
  branch), each pointed at Azurite via **explicit connection strings** (see the gotcha below),
  each publishing its co-hosted web port on a distinct host port so you can curl all three
  independently.
- `zitadel-db` - Postgres 16 (alpine), Zitadel's backing store. Healthchecked.
- `zitadel` - a single-container Zitadel (API + Console UI in one process - the pre-v3
  architecture; see "Why Zitadel v2.71.8, not the current v4 line" below), pinned to `v2.71.8`.
  Bootstraps one org + one service-account machine user automatically on first boot (see
  "Authenticated API walkthrough" below for what that does and doesn't get you). Published on
  host port `8084`.

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

## Authenticated API walkthrough (First Light)

`/api/v1/...` (work item #32/#33) requires a real, validly-signed Zitadel token whose "sub"
claim has been seeded into Case.Flow's tenant registry (`Flow.Api`'s `IdentityContextMiddleware`
-> `ITenantResolver` - see those types' remarks). Getting from "Zitadel container is running" to
"I have a bearer token Case.Flow will accept" needs a few **manual, one-time** steps - Zitadel's
own `FIRSTINSTANCE_*` bootstrap (this stack's `docker-compose.yml`) only goes as far as **one org
+ one service-account machine user**; it does not create the Case.Flow project/API application
(the thing that gives you an *audience*), a second org for tenant B, or any human/service users
you can actually mint a demo token for. Scripting those additional steps cleanly (Zitadel's
Management API, JWT-profile token exchange for the bootstrap machine key, etc.) without a live
Zitadel instance to verify against was assessed as too fiddly to land blind - see "What's
automated vs manual, and why" below for the full reasoning. This section documents the manual
path instead.

### What's automated already

- Postgres + Zitadel come up and Zitadel bootstraps itself: one org ("Case.Flow Eval"), one
  machine (service-account) user (`case-flow-eval-sa`) with a **machine key** (not a PAT - see
  below), written to `devops/eval/zitadel/machinekey/zitadel-admin-sa.json` on the host (bind-
  mounted from the container's `/machinekey`).
- `Flow.Silo`'s `Auth:Zitadel:Authority` is already wired to `http://zitadel:8080` (the compose
  service hostname - see "Why the issuer is the compose hostname, not localhost" below).
- `Program.cs`'s `SeedEvalIdentityRegistryAsync` (gated to `DOTNET_ENVIRONMENT=Docker`) reads two
  placeholder tenant slots from `EvalIdentity__Tenants__0__*` / `__1__*` env vars
  (`docker-compose.yml`) and seeds Case.Flow's tenant registry (`IIdentityRegistrySeeder` - the
  same seam `MultiTenantIsolationApiTests` uses) for any slot whose `Subject` is non-blank. Both
  slots ship with a **blank Subject**, so the stack boots cleanly with no seeding performed until
  you complete the steps below and fill them in.

### Manual steps

1. **Open the Zitadel Console** at `http://localhost:8084/ui/console` once the `zitadel`
   container is up (give it 20-30s on first boot for migrations). Log in with the
   `zitadel-admin-sa` machine user's key (Console -> "Service Users" login, or use the machine key
   JSON with a JWT-profile-capable client) - or, more simply for a one-off demo, create a human
   admin via the Console's own first-run invite flow if you'd rather not deal with the machine
   key at all.
2. **Create the Case.Flow project + an API application**: Console -> Projects -> New -> name it
   `Case.Flow`, then add an Application of type "API" (not "Web"/"Native" - this is a
   machine-to-machine resource, not something a browser redirects into). The application's
   **Client ID** is what `Auth__Zitadel__Audience` (and each `EvalIdentity__Tenants__<n>__Audience`)
   needs to be set to.
3. **Create a second org** (Console -> switch org -> "New Organization") for tenant B - the first
   bootstrapped org already stands in for tenant A.
4. **Create one human or service user per org** (tenant A's original org, and the new tenant B
   org), and obtain a token for each - either interactively (Console -> log in as that user ->
   inspect the token via a browser dev tool / the `/oidc/v1/userinfo` flow) or via Zitadel's
   client-credentials / password grant against the API application from step 2. Note each token's
   `sub` claim (decode the JWT - `https://jwt.io` or `python3 -c "import base64,json,sys;
   print(json.dumps(json.loads(base64.urlsafe_b64decode(sys.argv[1].split('.')[1]+'=='))))"
   <token>`).
5. **Fill in the compose env vars**: set `Auth__Zitadel__Audience` and both
   `EvalIdentity__Tenants__<n>__Subject` / `__Audience` in `docker-compose.yml` to the real values
   from steps 2 and 4, then `docker compose -f devops/eval/docker-compose.yml up -d` again to
   restart the silos with the new config (Zitadel/Postgres do not need to restart).
6. **Run the eval script**:
   ```
   TENANT_A_TOKEN="<tenant A's token>" TENANT_B_TOKEN="<tenant B's token>" \
     ./devops/eval/eval-authenticated.sh
   ```
   This deploys the flagship `.cmmn` sample, creates a case, reads it back as tenant A (expect
   200), then - if `TENANT_B_TOKEN` is set - reads the same case as tenant B (expect 404),
   demonstrating the isolation wall against the real containerized stack. `TENANT_B_TOKEN` is
   optional; omit it to verify only the authenticated happy path.

### Why the issuer is the compose hostname, not localhost

JwtBearer validates a token's `iss` claim against `ValidIssuer` (`AddFlowApi`'s
`TokenValidationParameters`) with an **exact string match**. Zitadel sets `iss` from its own
`ZITADEL_EXTERNALDOMAIN`/`EXTERNALPORT`/`EXTERNALSECURE` config. For that match to hold, whatever
address a client used to reach Zitadel and mint a token must be the SAME address the silo (also a
container on this compose network) uses as `Authority`. This stack sets
`ZITADEL_EXTERNALDOMAIN=zitadel` (the compose service hostname, resolved identically by every
container on this network) rather than `localhost` or a published host port - the tradeoff is
that a token minted by clicking through the Console at `http://localhost:8084` still carries
`iss=http://zitadel:8080` (matching what the silo expects), but any REDIRECT Zitadel's login UI
itself issues during an interactive browser flow will point at `http://zitadel:8080/...` URLs a
host browser cannot resolve (`zitadel` is only a valid hostname inside the compose network). This
does not block step 4 above (obtaining a token) - Zitadel's Console still serves its own UI
correctly at the published host port for non-redirect interactions - but a full interactive
OIDC authorization-code flow from a host browser is out of scope for this local eval stack. If
you need that, add a `127.0.0.1 zitadel` entry to your host's hosts file so `zitadel` resolves
identically from the host too, then use `http://zitadel:8084` instead of `localhost:8084`
everywhere.

### Why Zitadel v2.71.8, not the current v4 line

Zitadel's current stable release line (v4.x as of this writing) splits the login UI into its own
separate Next.js-based service behind a reverse proxy (Traefik in Zitadel's own official compose
example), plus optional Redis/OpenTelemetry services - meaningfully more moving parts than a
local eval stack needs, and a topology this work item could not verify live end-to-end within
its time budget. `v2.71.8` is the last widely-used pre-v3 release line, still a genuinely stable
(non-preview/non-rc) tag, and ships as a single container serving both the API and Console UI
(`start-from-init --masterkeyFromEnv --tlsMode disabled`) - verified directly against that
tag's own `docs/docs/self-hosting/deploy/docker-compose.yaml` /
`docker-compose-sa.yaml` in the `zitadel/zitadel` repository (image tags, env var names/shapes,
and the Postgres pairing below all confirmed against that source, not assumed). If a future
work item wants the current v4 topology, treat this as a from-scratch redesign of the `zitadel`
service (and possibly a Traefik-fronted reverse-proxy layer), not an incremental bump.

### What's automated vs manual, and why

Per this work item's own scope fence: P0 (the automated multi-tenant isolation test suite -
`Flow.Grains.Tests.Integration/Api/MultiTenantIsolationApiTests`) is the hard, CI-critical
requirement and is fully automated and green. This Zitadel/Postgres infrastructure is explicitly
best-effort "First Light demo infra." Everything above the "Manual steps" line is real,
committed, working configuration (compose services, image tags, `Auth:Zitadel` wiring, the
Docker-gated tenant-registry seeding code) - verified against Zitadel's own source at the pinned
tag and validated with `docker compose config`. The steps below that line (project/application
creation, a second org, human/service users, token acquisition) require Zitadel's Management
API/Console UI in ways that could not be scripted and verified against a live instance within
this work item's time budget without risking a subtly-broken "looks automated but silently
fails" script - landing them as clear, one-time manual steps was judged the more honest and more
maintainable choice than forcing brittle automation. A future work item could script steps 1-4 via
Zitadel's Management API using the bootstrapped machine key's JWT-profile grant (`internal/api`
docs, `zitadel-tools`, or a small Go/Node helper) once someone can iterate against a live
instance.

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

(`-v` also drops the `azurite-eval-data` **and** `zitadel-eval-db-data` volumes, so the next
bring-up starts from a clean membership table and a fresh Zitadel bootstrap - you will need to
redo the "Manual steps" above after a `-v` teardown. The Zitadel machine key written to
`devops/eval/zitadel/machinekey/` on the host is NOT removed by `down -v` - delete that directory
yourself if you want a fully clean slate.)

## Notes

- Azurite publishes **no host ports** - the silos reach it entirely over the internal Docker
  network via the `azurite` service hostname (container-internal ports 10000/10001/10002). This
  keeps the eval stack from colliding with any other Azurite a developer runs for other projects;
  no host-side tool (e.g. Storage Explorer) is expected to connect. (The silo containers still
  publish their web ports 8081-8083 so you can `curl` the cluster-status endpoint from the host;
  Zitadel similarly publishes 8084 since the manual bootstrap steps need host reachability.)
- The case-operation REST API itself (`/api/v1/...` - #32/#33) is in scope for this stack as of
  sub-unit 4 - see "Authenticated API walkthrough" above. Still out of scope: the Quartz/timer
  redesign (#31).
- The automated, CI-critical proof that multi-tenant isolation actually works lives in
  `src/Flow.Grains.Tests.Integration/Api/MultiTenantIsolationApiTests.cs` (an in-memory
  `Microsoft.AspNetCore.TestHost` over the same `Flow.Api` composition this stack runs in
  containers, with a test authentication handler standing in for Zitadel) - it runs in every
  `dotnet test`, with no Docker/Zitadel dependency. This stack's authenticated walkthrough is a
  demo of the same property against the real containerized/real-token path, not a substitute for
  that suite.
