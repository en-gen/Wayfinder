# Modernization Plan

*Assessed April 2026. Target: shippable multi-tenant Azure SaaS product.*

---

## Objective

Modernize Case-Flow from a ~2016 development-only CMMN engine prototype into a production-grade, multi-tenant, cloud-native SaaS product running on Azure. The engine implementation is substantially complete — the work is modernizing the stack, implementing production infrastructure, building the API surface, and filling gaps in the CMMN implementation.

---

## Current State vs Target State

| Dimension | Current | Target |
|---|---|---|
| Runtime | .NET Core 3.1 (EOL 2022) | .NET 8 LTS |
| Orleans | 3.3.0 (~2020) | 8.x |
| Serialization | `[Serializable]` (BinaryFormatter) | `[GenerateSerializer]` (Orleans codegen) |
| JSON library | Newtonsoft.Json | System.Text.Json |
| Clustering | In-memory localhost | Azure Table Storage |
| Grain state | In-memory | Azure Blob Storage |
| Event log | In-memory | Durable event store |
| Stream provider | SimpleMessageStream (in-memory) | Azure Service Bus |
| Timer scheduling | Quartz (volatile RAM) + Orleans Reminders (keepalive only) | Orleans Reminders (full replacement) |
| REST API | None | ASP.NET Core with OpenAPI |
| Authentication | None | JWT / Auth0 |
| Multi-tenancy | Implicit (grain keys only) | Enforced middleware + explicit context |
| Observability | Serilog only | OpenTelemetry + Application Insights |
| CaseFileItem | Model only, no grain | Grain backed by Azurite (dev) / Azure Blob (prod) |
| Planning table | Partial | Complete |
| Containerization | None | Docker + Azure Container Apps |
| Modeler UI | None | cmmn-js web shell (v2 — concierge onboarding for v1) |

---

## Phase 1 — Stack Modernization

*Prerequisite for all subsequent work. Everything else builds on a modern foundation.*

### .NET 8 Upgrade

Mechanical but required. Target `net8.0` across all projects. Expect minor API changes and namespace updates.

**Effort:** 3–5 days | **Risk:** Low

### Orleans 3.3 → 8.x

The highest-risk item in the modernization. Orleans 8 introduced breaking changes across:
- Serialization system (`[Serializable]` → `[GenerateSerializer]` with source generation)
- Stream provider API
- Silo host configuration (new `ISiloBuilder` patterns)
- Clustering and persistence provider registration

**Serialization is the critical risk.** The 40 `[Serializable]` event and state classes must be migrated to Orleans 8's codegen serializer. If event serialization breaks, the event log becomes unreadable and grain state cannot be hydrated. Write migration tests that confirm events replay to identical state before and after the migration.

```csharp
// Before (Orleans 3.x)
[Serializable]
public class Transitioned : BaseEvent { ... }

// After (Orleans 8.x)
[GenerateSerializer]
public class Transitioned : BaseEvent
{
    [Id(0)] public PlanItemTransition Transition { get; set; }
    [Id(1)] public PlanItemState NewState { get; set; }
}
```

**Effort:** 2–3 weeks | **Risk:** HIGH — write serialization regression tests first

### Replace Newtonsoft.Json with System.Text.Json

Newtonsoft.Json is used in two distinct areas:

**1. Model attributes (`Spec.CMMN.MODEL.cs`, `PlanningTable.cs`)** — 25 `[Newtonsoft.Json.JsonIgnoreAttribute]` usages. Straightforward replacement with `[System.Text.Json.Serialization.JsonIgnore]`.

**2. Expression evaluation bridge (`Executable.cs`, `JObjectInstance.cs`, `JObjectPropertyDescriptor.cs`, `JTokenExtensions.cs`)** — Newtonsoft `JObject`/`JToken` are used to pass data into Jint's JavaScript engine via a hand-rolled `JObjectInstance : ObjectInstance` bridge.

The Jint 3.x upgrade resolves this naturally. Jint 3.x has built-in `System.Text.Json` support — `JsonElement` can be passed directly into the engine without a custom bridge class. The `JObjectInstance` and `JObjectPropertyDescriptor` classes can be deleted and replaced with Jint 3.x's native JSON integration.

**Effort:** 1 week (mostly Jint 3.x upgrade work) | **Risk:** Medium — test expression evaluation thoroughly

### Replace Quartz with Orleans Reminders

Quartz.NET is used for the actual timer scheduling (job creation, ISO 8601 trigger configuration, repetition). Orleans Reminders are currently used only as a keepalive to prevent `TimerEventSchedulerGrain` from being deactivated — `ReceiveReminder()` does nothing except log.

**Replace both with proper Orleans Reminder-based scheduling:**
- Delete `QuartzJobFactory`, `QuartzSchedulerConfig`, `QuartzSchedulerFactory`, `TimerTickJob`
- Remove Quartz NuGet package entirely
- Implement `ReceiveReminder()` to fire the timer event
- Use the existing `Iso8601` class for duration/interval parsing to determine reminder period
- Store timer state (next fire time, repetition count) in `TimerEventSchedulerStore`

Orleans Reminders are durable by design (backed by Azure Table Storage in production) — this eliminates the volatile timer problem without adding a SQL Server dependency.

**Effort:** 1 week | **Risk:** Low — the Iso8601 parsing is already done; only the job/trigger abstractions need replacing

### Package Updates

| Package | Current | Action |
|---|---|---|
| Jint | 2.11.58 | Upgrade to 3.x — significant API changes; aligns with System.Text.Json |
| AutoMapper | 10.0.0 | Upgrade to 13.x |
| Newtonsoft.Json | 12.0.3 | **Remove** — replace with System.Text.Json |
| Quartz | 3.1.0 | **Remove** — replace with Orleans Reminders |
| Stateless | 5.1.3 | Upgrade to latest 5.x |
| xUnit / Moq / AutoFixture | Various | Upgrade to latest |

**Effort:** 3–5 days (excluding Jint/Newtonsoft, covered above) | **Risk:** Low

### Update Test Infrastructure

`ClusterFixture` uses `SiloHostBuilder` patterns from Orleans 3.x. Update to Orleans 8 `TestClusterBuilder`. Remove Quartz registration from test host. Revalidate all integration tests pass.

**Effort:** 2–3 days | **Risk:** Low

**Phase 1 Total: 5–6 weeks**

---

## Phase 2 — Containerization

*Docker-first from the start. Required for Azure Container Apps deployment.*

### Dockerfile

The silo is a straightforward ASP.NET Core application — standard multi-stage Dockerfile:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Flow.Silo/Flow.Silo.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Flow.Silo.dll"]
```

**Effort:** 1–2 days | **Risk:** Low

### Docker Compose (Development)

Local development environment with all dependencies:

```yaml
services:
  silo:
    build: .
    environment:
      - ORLEANS_ENVIRONMENT=Development
    depends_on:
      - azurite

  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    ports:
      - "10000:10000"  # Blob
      - "10001:10001"  # Queue
      - "10002:10002"  # Table
```

Seq is intentionally excluded — it runs as a shared external Docker container across all projects on the developer's machine, not per-project. Configure the Serilog Seq sink in `appsettings.Development.json` to point at the shared Seq instance (typically `http://localhost:5341`).

Azurite replaces all Azure Storage dependencies locally:
- Azure Table Storage → Azurite Table (Orleans clustering)
- Azure Blob Storage → Azurite Blob (grain state, event log, CaseFileItem storage)
- Azure Queue Storage → Azurite Queue (if needed)

**Effort:** 2–3 days | **Risk:** Low

### Azure Container Apps Configuration

Orleans has specific requirements in a containerised environment that differ from a typical stateless ASP.NET Core service.

#### Clustering

Each silo registers its container IP and port `11111` in the Azure Table Storage membership table on startup. Other silos read the table to discover peers and route grain calls. ACA replicas within the same Container Apps Environment can reach each other by IP on any port — no special networking configuration is required for silo-to-silo traffic.

#### Minimum Replicas — Never Scale to Zero

Orleans is stateful. Active reminders, in-flight stream events, and grain activations all live in-memory. Scaling to zero stops all reminder firing and destroys the cluster.

| Environment | `minReplicas` |
|---|---|
| Production | **2** |
| Staging | 1 |
| Development | Docker Compose (no ACA) |

Two production replicas provides a healthy cluster, redundancy during rolling deployments, and continuous reminder firing. CPU-based autoscaling handles scale-out from there; KEDA Service Bus queue depth can be added later for more accurate scaling.

#### Port Configuration

```
HTTP API  (port 8080)  → External ingress  — user-facing
Silo port (port 11111) → No ingress needed — internal replica-to-replica only
Gateway   (port 30000) → No ingress needed — Orleans client is co-hosted in-process
```

Because the Orleans client is co-hosted in the same silo process, there is no external gateway traffic.

#### Rolling Deployments

ACA starts new replicas before stopping old ones. Orleans handles this gracefully — the new silo joins the cluster, grains spread across old and new silos, then old silos deactivate and deregister. Configure a termination grace period long enough for graceful grain deactivation:

```bicep
terminationGracePeriodSeconds: 60
```

#### Health Probes

Orleans takes a few seconds to join the cluster after startup. Use separate liveness and readiness probes to prevent ACA routing traffic to a silo that hasn't joined yet:

```
Liveness:   GET /health/live   — silo process is running
Readiness:  GET /health/ready  — silo has joined the Orleans cluster
```

Orleans 8 provides a built-in `SiloHealthCheck` that can be wired to ASP.NET Core's health check middleware and surfaced on these endpoints.

#### Bicep Sketch

```bicep
resource siloApp 'Microsoft.App/containerApps@2024-03-01' = {
  properties: {
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
      }
      terminationGracePeriodSeconds: 60
    }
    template: {
      containers: [
        {
          image: 'your-acr.azurecr.io/case-flow-silo:latest'
          env: [
            { name: 'ORLEANS_CLUSTER_ID',      value: 'case-flow-prod' }
            { name: 'ORLEANS_SERVICE_ID',       value: 'case-flow' }
            { name: 'AZURE_STORAGE_CONNECTION', secretRef: 'storage-connection' }
          ]
          probes: [
            { type: 'Liveness',  httpGet: { path: '/health/live',  port: 8080 } }
            { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 } }
          ]
        }
      ]
      scale: {
        minReplicas: 2
        maxReplicas: 10
        rules: [
          {
            name: 'cpu-scaling'
            custom: {
              type: 'cpu'
              metadata: { type: 'Utilization', value: '70' }
            }
          }
        ]
      }
    }
  }
}
```

**Effort:** Covered in Phase 3 Bicep work

**Phase 2 Total: 1 week**

---

## Phase 3 — Production Infrastructure

*You cannot ship without durable state. Current production config throws `NotImplementedException`.*

### Implement `ConfigureDeployedOrleans()`

```csharp
// Currently:
private static void ConfigureDeployedOrleans(HostBuilderContext context, ISiloBuilder silo)
{
    throw new NotImplementedException();
}

// Target:
silo
    .UseAzureStorageClustering(options => options.ConfigureTableServiceClient(...))
    .AddAzureBlobGrainStorageAsDefault(options => ...)
    .AddAzureBlobGrainStorage("LogStorage", options => ...)  // JournaledGrain event log
    .AddServiceBusStreams("Default", options => ...)
    .UseAzureTableReminderService(options => ...);           // durable reminders for timers
```

**Effort:** 1 week | **Risk:** Medium

### Development Storage (Azurite)

`ConfigureDevelopmentOrleans()` switches all Azure Storage references to Azurite connection strings. Developers need only Docker Compose running — no Azure subscription required for local development.

```csharp
// Development: point everything at Azurite
var azuriteConnection = "UseDevelopmentStorage=true";
silo
    .UseAzureStorageClustering(opt => opt.ConfigureTableServiceClient(azuriteConnection))
    .AddAzureBlobGrainStorageAsDefault(opt => opt.ConfigureBlobServiceClient(azuriteConnection))
    // ...
```

**Effort:** 1–2 days (part of the above) | **Risk:** Low

### Stream Provider

`SimpleMessageStreamProvider` is fire-and-forget in-memory. Unacceptable for production — a silo restart loses all in-flight sentry events.

**Decision: Azure Service Bus.** Crunchafi already uses it across products. Durable, Azure-native, familiar operational model. Complex cases can generate many stream events per transition (each sentry subscription fires) — model expected throughput before sizing.

**Effort:** 3–5 days | **Risk:** Medium

### Azure Infrastructure (Bicep)

Provision:
- Azure Container App (Orleans silo)
- Azure Container Registry
- Azure Storage Account (Table for clustering + Reminder service; Blob for grain state, event log, CaseFileItem)
- Azure Service Bus namespace + topics
- Azure Key Vault
- Application Insights + Log Analytics workspace

Reuse Crunchafi's established Bicep module patterns.

**Effort:** 1 week | **Risk:** Low

### Event Log Storage

The `JournaledGrain` log consistency provider (`"LogStorage"`) needs a durable backing store in production. Use Azure Blob Storage — same account as grain state, separate container. This keeps infrastructure simple while providing independent retention control.

```
Storage Account
  ├─ Container: grain-state     ← current grain state snapshots
  ├─ Container: event-log       ← JournaledGrain event sequences
  └─ Container: case-files      ← CaseFileItem blobs
```

Retain the full event log indefinitely — it is the source of truth for the user-facing audit trail and point-in-time reconstruction (see below). Do not configure TTL on the event-log container.

**Effort:** Covered in `ConfigureDeployedOrleans()` work above | **Risk:** Low

### Read Model — SQL Projection

The Orleans event log is the internal source of truth but is not directly queryable for user-facing features. A SQL read model projected from the event stream provides efficient queries for task queues, case lists, dashboards, and audit trail views.

**Tables:**

```sql
-- Case summary (current state)
Cases         (Id, TenantId, DefinitionId, DefinitionVersion, State, CreatedAt, UpdatedAt)

-- Append-only event timeline — never updated or deleted
CaseEvents    (Id, EventId, TenantId, CaseId, SequenceNumber, EventType,
               PlanItemId, PlanItemName, PlanItemType,
               FromState, ToState, Transition, TriggeredBy,
               UserId, OccurredAt, Payload)

-- Current plan item states
PlanItems     (Id, TenantId, CaseId, Name, ItemType, State, UpdatedAt)

-- Human task queue
Tasks         (Id, TenantId, CaseId, Name, State, AssignedTo, ClaimedAt, CompletedAt, CreatedAt)

-- Idempotency tracking
ProcessedEvents (EventId, ProcessedAt)

-- Point-in-time snapshots (see below)
CaseSnapshots (Id, TenantId, CaseId, SnapshotAt, SequenceNumber, StateJson)
```

`CaseEvents.SequenceNumber` maps to the Orleans grain event version — provides unambiguous ordering even when two events share the same millisecond timestamp.

Index `(CaseId, OccurredAt)` on `CaseEvents` for efficient point-in-time queries.

**Effort:** 2–3 weeks (schema + EF Core models + projection service) | **Risk:** Low

### Projection Service

A hosted service (initially co-hosted in the silo, separable later) that subscribes to the Orleans stream via Service Bus and writes to SQL:

```
Service Bus
    ↓
ProjectionService (IHostedService)
    ├─ PlanItemTransitionedEvent  → UPDATE PlanItems + INSERT CaseEvents
    ├─ CaseCreatedEvent           → INSERT Cases
    ├─ MilestoneReachedEvent      → INSERT CaseEvents
    └─ HumanTask → Available      → INSERT/UPDATE Tasks
```

**Idempotency is required.** Service Bus delivers at-least-once. Before writing any projection, check `ProcessedEvents` for the `EventId`. Skip if already processed. This makes the projection safe to replay after failure.

**Effort:** Included in the 2–3 weeks above | **Risk:** Low

### Point-in-Time Reconstruction

Because `CaseEvents` is append-only and sequenced, the system can reconstruct the exact state of any engagement at any point in time:

```sql
SELECT *
FROM   CaseEvents
WHERE  CaseId   = @caseId
AND    OccurredAt <= @asOf
ORDER  BY SequenceNumber ASC
```

For long-running cases with large event volumes, periodic snapshots bound reconstruction cost:

1. Find the most recent `CaseSnapshot` with `SnapshotAt <= @asOf`
2. Replay `CaseEvents` from `snapshot.SequenceNumber` up to `@asOf`
3. Apply events to the snapshot state

This is the same pattern Orleans uses internally for grain hydration.

**Effort:** 1 week (snapshot writer + reconstruction API endpoint) | **Risk:** Low

### Observability

Orleans 8 has built-in OpenTelemetry support. Add Application Insights export and instrument:
- Grain activation/deactivation counts
- Case instance lifecycle events
- Sentry evaluation latency
- Timer reminder scheduling

**Effort:** 2–3 days | **Risk:** Low

**Phase 3 Total: 5–7 weeks**

---

## Phase 4 — Multi-Tenancy Hardening

*Can overlap with Phase 3.*

### Eliminate `CaseRequestContext` Static Ambient

Replace 22 references to the static ambient context. Pass tenant/user context explicitly through grain method parameters or use a properly scoped `AsyncLocal<T>` wrapper populated by HTTP middleware.

```csharp
// Before
var tenantId = CaseRequestContext.TenantId;

// After — pass through activation/method args
public Task Create(Guid caseDefinitionId, CaseContext context) { ... }
```

**Effort:** 1 week | **Risk:** Medium — touches core grain interfaces

### Tenant Enforcement Middleware

HTTP middleware that:
1. Validates JWT and extracts claims
2. Resolves tenant from claims (align with Carina/Auth0 if integrating with Crunchafi)
3. Populates scoped context for the request
4. Validates the requesting user belongs to the tenant they're addressing

**Effort:** 3–5 days | **Risk:** Low

**Phase 4 Total: 2–3 weeks**

---

## Phase 5 — REST API

*The entire user-facing surface. Currently nothing exists.*

### Orleans Client Integration

Co-host an Orleans client in the ASP.NET Core silo. HTTP requests call grain interfaces via the client; Orleans handles routing to the correct grain activation.

### Endpoints Required

**Case Definitions**
```
POST   /definitions                    — deploy a new case definition (CMMN XML)
GET    /definitions                    — list deployed definitions
GET    /definitions/{id}               — get definition
PUT    /definitions/{id}               — update definition (new version)
DELETE /definitions/{id}               — retire definition
```

**Case Instances**
```
POST   /cases                          — create a new case instance
GET    /cases                          — list cases (with filter/pagination)
GET    /cases/{id}                     — get case snapshot
POST   /cases/{id}/terminate           — terminate a case
GET    /cases/{id}/plan-items          — list all plan items and states
```

**Human Task Queue**
```
GET    /tasks                          — list tasks assigned to me / my team
POST   /tasks/{id}/claim               — claim a task
POST   /tasks/{id}/complete            — complete a task (with output data)
POST   /tasks/{id}/reassign            — reassign to another user
POST   /tasks/{id}/release             — release a claimed task back to pool
```

**Events**
```
POST   /cases/{id}/events              — inject a user event (trigger UserEventListener)
```

**Webhooks**
```
POST   /webhooks                       — register a webhook endpoint
GET    /webhooks                       — list registered webhooks
DELETE /webhooks/{id}                  — remove webhook
```

Webhook events: `case.created`, `case.completed`, `case.terminated`, `planitem.activated`, `planitem.completed`, `task.available`, `milestone.reached`.

**Effort:** 6–8 weeks | **Risk:** Low (well-understood engineering)

The human task queue is the most product-sensitive endpoint. Claim/assignment/notification semantics require product decisions, not just implementation.

**Phase 5 Total: 6–8 weeks**

---

## Phase 6 — Engine Gaps

*Fill missing CMMN implementation.*

### CaseFileItem Grain

The model, event class, and sentry handler stubs exist but no grain is implemented. Without `CaseFileItem`, sentries cannot react to data changes — entry criteria can only respond to plan item lifecycle events.

**Initial implementation uses Azure Blob Storage (Azurite in development):**
- Each `CaseFileItem` grain manages a blob reference in Azure Blob Storage
- Grain stores blob metadata (name, content type, URI, state) in its event-sourced state
- File upload goes through the API → blob written to storage → grain transitions state → sentries evaluate
- Azurite in Docker Compose provides the local blob endpoint; same code runs against Azure Blob in production with no changes

Required:
- `ICaseFileItemGrain` interface
- `CaseFileItemGrain` implementation
- `CaseFileItemStore` with event application
- Blob storage helper (upload, read, delete)
- Wire into `SentryGrain.HandleCaseFileItemTransitioned()`
- API endpoints: `POST /cases/{id}/files`, `GET /cases/{id}/files/{itemId}`

**Effort:** 1–2 weeks | **Risk:** Low

### Planning Table (Discretionary Items)

`PlanningTableGrain` is partially implemented. Complete the logic for:
- Listing available discretionary items
- Worker-initiated item creation
- Applicability rule evaluation

**Effort:** 1–2 weeks | **Risk:** Low

### Error Recovery

No compensation mechanism exists. Define behavior for:
- Task failure (`Fault` transition) — how does the case respond?
- Partial stage failure — can the stage continue or must it terminate?
- Dead letter handling for undeliverable stream events

**Effort:** 1 week | **Risk:** Medium

**Phase 6 Total: 3–5 weeks**

---

## Phase 7 — Modeler UI

*v2 feature — deliberately deferred from initial launch.*

For the design partner phase, workflows are authored by the Crunchafi team using standard open source CMMN tooling (cmmn-js) and loaded into the platform via the case definition API. Design partners describe their methodology; Crunchafi encodes it. This concierge approach ships v1 faster and ensures the self-service modeller is built around validated real-world templates rather than assumptions. The modeller is built once those templates exist.

*Can be developed in parallel with Phase 5/6 once design partner templates are underway.*

### cmmn-js Integration

[cmmn-js](https://github.com/bpmn-io/cmmn-js) is the open-source CMMN diagram editor maintained by Camunda (the same team behind bpmn-js). It provides a full WYSIWYG modeler for CMMN diagrams with export to CMMN XML.

Integration work:
- Scaffold a React or Vue 3 web application
- Embed `cmmn-js` modeler
- Wire save/load to the case definition API
- Add deployment action (POST `/definitions`)
- Version history / rollback UI

**Effort:** 2–3 weeks | **Risk:** Low — cmmn-js is battle-tested

### Case Template Library

Pre-built CMMN case models that lower time-to-value for specific verticals:
- Financial audit engagement
- HR case management
- Legal matter management
- Insurance claims

**Effort:** 1–2 weeks (content, not engineering) | **Risk:** Low

**Phase 7 Total: 5–7 weeks**

---

## Timeline Summary

| Phase | Work | Effort | Dependency |
|---|---|---|---|
| 1 | Stack modernization | 5–6 weeks | None — start here |
| 2 | Containerization | 1 week | Can start alongside Phase 1 |
| 3 | Production infrastructure | 3–4 weeks | After Phase 1 |
| 4 | Multi-tenancy hardening | 2–3 weeks | Can overlap Phase 3 |
| 5 | REST API | 6–8 weeks | After Phase 1 |
| 6 | Engine gaps (CaseFileItem, planning table) | 3–5 weeks | Can overlap Phase 5 |
| 7 | Modeler UI | 5–7 weeks | After Phase 5 (needs API) |

### Delivery Scenarios

| Team | v1 (engine + API + dashboard + task inbox) | v2 (adds self-service modeller) |
|---|---|---|
| 1 developer | 4–5 months | 6–8 months |
| 2 developers | 2.5–3 months | 4–5 months |
| 3 developers (original author as lead) | 2–3 months | 3–4 months |

The v1 estimate assumes the original author leads the effort in an architect and technical lead capacity. The modeller (Phase 7) ships in v2, informed by templates built during the design partner concierge phase.

### Biggest Risks

1. **Orleans 3→8 serialization migration** — corrupted event logs if not done carefully. Mitigate by writing serialization regression tests before migrating.
2. **Jint 3.x + System.Text.Json bridge** — the hand-rolled `JObjectInstance` needs replacing; test all expression evaluation scenarios after migration.
3. **Stream provider at scale** — a complex case with many sentries generates many messages. Load test before production.
4. **Human task queue product design** — the API shape depends on product decisions (notification model, assignment rules, SLA tracking). Don't build until these are defined.

---

## If Building Within Crunchafi

Crunchafi advantages that compress this timeline:

| Asset | Value |
|---|---|
| Bicep IaC patterns | Phase 3 infrastructure is largely a copy-adapt exercise |
| Auth0 / Carina integration | Phase 4 auth middleware aligns with existing identity spine |
| Azure Service Bus already in use | Stream provider decision is already made |
| Azurite already used in dev environments | Docker Compose pattern is established |
| ScrumBot | Can accelerate mechanical work (serialization migration, boilerplate endpoints) |
| Existing Azure subscriptions and ACR | No net-new infrastructure setup |

Crunchafi-specific additions beyond the base plan:
- **Carina connector** — register the CMMN platform as a product in Carina (orgs, users, entitlements)
- **Crunchafi Lease Accounting ProcessTask** — built-in ProcessTask implementation that invokes Crunchafi Lease Accounting APIs
- **Crunchafi Data Extraction ProcessTask** — built-in ProcessTask implementation that invokes Crunchafi Data Extraction data extraction
- **Audit case templates** — pre-built engagement models aligned with Thomson Reuters Guided Assurance workflow
