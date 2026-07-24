# Orleans Feature-Provider Performance & Scalability Evaluation

*Time-boxed research spike for work item #73. No empirical benchmarks were run — this
environment has no Docker/infra available. §7 designs the reproducible harness that would
validate this analysis later, aligned with the Testcontainers pattern work item #60 already
established (`AzuriteClusterFixture`, `DockerAvailability`, `RequiresDockerFact`). Companion to
[07-product-roadmap.md](07-product-roadmap.md) M2 ("runs somewhere real") and the #35 benchmark
milestone (10k concurrent cases × 50 plan items). Findings below are grounded in direct
inspection of `Flow.Silo/Program.cs` / `AzureOptions.cs` plus Microsoft Learn documentation and
Orleans project docs/source; every claim is tagged **[E]** (evidence-backed, source linked) or
**[R]** (reasoned/engineering judgment, no authoritative source found) — see the ledger in §8.*

---

## 1. Executive Summary

**Verdict: harden the "one Azure Storage account" default as-is for M0/M1/M2 — no subsystem
clears the bar for switching providers today.** The one finding worth acting on isn't "switch
X for Y," it's that two pieces of the *stated* default aren't actually implemented yet
(§2) — that gap matters more than any provider swap.

| Subsystem | Current (verified in code) | Recommendation | Why (one line) |
|---|---|---|---|
| Clustering/membership | Azure Table (deployed) / Localhost (dev) | **Keep Azure Table** | Membership writes are silo-count-bound, not backend-bound (§4) — Case.Flow will never get near the ceiling |
| Grain storage (journaled state) | Azure Blob (Azurite in dev) | **Keep Azure Blob**; ADO.NET/SQL is the only credible future challenger, gated on M2's query needs | Blob-per-grain sidesteps Table's partition ceiling entirely; SQL only pays off if cross-grain queries or transactions become a real requirement |
| Reminders | Azure Table (deployed) / InMemory (dev) | **Keep Azure Table**; revisit whether *Orleans Reminders at all* (vs. durable Quartz) is the right timer subsystem before touching the provider | 1-minute reminder floor is an Orleans-wide constraint, not a provider one |
| Streams | **In-memory, fire-and-forget** (not Azure Queue — see §2) | **Azure Queue Storage** as the first durable provider | Matches the account default, fixes a real correctness bug (doc 06), no replay requirement exists to justify Event Hubs |

Nothing here is close enough to justify fragmenting the storage story before there's a measured
reason. The ADO.NET/SQL path for grain storage is flagged as the one genuinely open question —
not because Azure Storage is provably worse, but because nobody has measured either one against
Case.Flow's actual activation-storm shape (doc 06 §3.3).

---

## 2. Current State (grounded in code, not the brief)

Direct inspection of `src/Flow.Silo/Program.cs` and `src/Flow.Silo/Infrastructure/Options/AzureOptions.cs`
turned up two gaps between the task's framing of "the current default" and what's actually
wired up. Both are worth a line in the record since they change what this evaluation is really
comparing:

- **Clustering + reminders**: `UseAzureStorageClustering` + `UseAzureTableReminderService`,
  both riding the same DI-resolved `TableServiceClient` (`ConfigureDeployedOrleans`,
  `Program.cs:254-258`) — matches the brief.
- **Grain storage**: `AddAzureBlobGrainStorageAsDefault` for the journaled/log-consistency slot
  (`Program.cs:149`, Azurite locally) — matches the brief.
- **PubSubStore (stream pub-sub metadata)**: `AddMemoryGrainStorage("PubSubStore", ...)`
  (`Program.cs:151`) — **in-memory in both environments today**, not Table/Blob. The brief's
  "durable PubSubStore on Table/Blob" describes an intended, not yet implemented, state.
- **Streams**: `AddMemoryStreams("Default")` (`Program.cs:152`) — **in-memory in both
  environments today**, not Azure Queue. This is a known correctness gap: with fire-and-forget
  stream delivery (`FireAndForgetDelivery = true`), a deactivated sentry silently misses
  transition events and stays unsatisfied forever. The roadmap item that fixes it — replacing
  fire-and-forget streams for criticality-1 events — hasn't landed.

Practically: this memo's Streams section (§5.4) is evaluating **what should replace memory
streams**, not swapping between two already-durable options. Everything else — clustering,
grain storage, reminders — genuinely is on Azure Storage today, deployed-path only (Development
uses `UseLocalhostClustering` / `UseInMemoryReminderService`, correctly excluded from this
production-provider comparison).

All packages are pinned to `Microsoft.Orleans.*` 10.2.1 on net10.0 across the solution.

---

## 3. Scope and Method

This is a desk evaluation: codebase inspection + published documentation + reasoned engineering
judgment. No load was generated against any provider. Where Microsoft Learn or Orleans project
docs state a concrete number (throughput ceiling, RU cost, connection-pool default), it's cited
inline and tagged **[E]**. Where no authoritative source exists — most latency figures, most
cost-at-scale figures, anything about Case.Flow's specific access pattern under load — it's
reasoned from the documented mechanics and tagged **[R]**. Nothing here should be read as a
substitute for the benchmark run designed in §7.

**Target workload silhouette** (from #35, restated in `docs/06` §3.3 and `docs/07` M2.6):
10,000 concurrent cases × 50 plan items ≈ 500,000 `PlanItemGrain` activations, plus a
`CaseGrain`, `SentryGrain`s, and (per `docs/06`'s GitNexus cluster counts — Behaviors 169
symbols, PlanItem 80, Case 50, Sentry 20) proportionally more grains layered on top. `docs/06`
§3.3 already flags the mechanism that matters: *"stage activation fans out sequential grain
round-trips per child, each with sentry/stream subscriptions persisted to PubSubStore — an
activation storm at scale."* That's the shape this evaluation reasons against.

**Dimensions evaluated per subsystem**: throughput; latency (p50/p99); scalability ceiling
(documented limits, not vibes); operational complexity; cost at target scale; consistency /
transactional guarantees; failure & recovery behavior.

---

## 4. Cross-cutting finding: clustering scalability is silo-count-bound, not grain-count-bound

This is the single most load-bearing fact in the whole evaluation, and it's easy to miss if you
only look at raw provider throughput numbers.

Every official `IMembershipTable` implementation — Azure Table, ADO.NET/SQL, Redis, Cosmos DB,
DynamoDB, ZooKeeper, Cassandra — funnels every membership *write* (a suspicion, a death
declaration, a join) through **a single membership-version row/column that serializes all
updates**, by design, regardless of backend:

> *"A special membership-version row in the table tracks changes... All writes are serialized
> through this row using atomic updates... Serializing all writes through the version row can
> impact scalability due to increased contention. The protocol has proven effective in
> production with up to 200 silos but might face challenges beyond a thousand silos."*
> — [Cluster management in Orleans, Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/cluster-management) **[E]**

Crucially, silo-to-silo failure detection itself is direct peer-to-peer TCP, not routed through
the table at all — *"Traffic to the table is very light... probes go directly between servers,
not to the table"* **[E]**, same source. So the membership backend's throughput ceiling (Azure
Table's well-known 2,000 entities/sec/partition — [Table storage scalability targets,
Microsoft Learn](https://learn.microsoft.com/en-us/azure/storage/tables/scalability-targets)
**[E]**) is never actually stressed by case/grain volume. It's stressed by *silo count* and
*churn rate* — and Case.Flow, even at the 10k-case target, is a handful to low dozens of silos,
not hundreds. **Grain-count and case-count scale (the thing #35 is actually measuring) has
essentially no bearing on clustering-provider choice.** The provider that wins here should be
picked on operational grounds, not throughput — and Azure Table already wins that: zero new
infrastructure, and `AzureTableClusteringTests` in `Flow.Grains.Tests.Integration` already
validates real multi-silo membership against it.

This reframes the rest of the matrix: clustering and reminders are low-stakes (§5.1, §5.3);
grain storage and streams are where the actual case/grain-scale load lands (§5.2, §5.4).

---

## 5. Provider Matrix

### 5.1 Clustering / Membership — Azure Table vs. ADO.NET (SQL Server/PostgreSQL) vs. Redis

| Dimension | Azure Table (current) | ADO.NET / SQL Server | Redis |
|---|---|---|---|
| Throughput | Irrelevant at Case.Flow's silo scale (§4) **[E+R]** | Same — same version-row protocol, different backend **[R]** | Same, but see durability note below **[E]** |
| Latency | Doesn't matter — probes bypass the table; only administrative reads/writes touch it **[E]** | Marginally lower per-call latency in-VNet, immaterial given probe design **[R]** | Lowest raw latency, immaterial for the same reason **[R]** |
| Scalability ceiling | Proven to 200 silos in MS production, "challenges beyond a thousand" — same ceiling for all backends **[E]** | Same ceiling (protocol-level, not backend-level) **[E]** | Same ceiling, **plus** membership data must be explicitly durable: *"Implementations of the IMembershipTable interface must use a durable data store... Volatile configurations may result in cluster unavailability"* **[E]** |
| Operational complexity | Zero new infra — already provisioned for reminders/journal | New Azure SQL/SQL Server resource, schema via Orleans' shipped `CreateOrleansTables_SqlServer.sql`, connection/identity management | New Azure Cache for Redis resource; persistence (AOF/RDB) must be turned on explicitly — off by default in cheaper tiers |
| Cost at target scale | Effectively free (already-provisioned account) | Azure SQL minimum vCore/DTU spend | Standard/Premium tier required for persistence — meaningfully more than Table |
| Consistency | Optimistic concurrency via ETags, serializable batch transactions per partition **[E]** | Optimistic concurrency via transactions/ROWVERSION, functionally equivalent **[E]** | Functionally equivalent once persistence is correctly configured |
| Failure & recovery | Already validated locally (`AzureTableClusteringTests` vs. Azurite) | Would need new integration coverage | Would need new integration coverage + persistence-loss testing |

**Recommendation: keep Azure Table.** No dimension favors switching, and §4 shows the theoretical
advantage of the alternatives (raw throughput/latency) is never actually exercised by Case.Flow's
workload.

### 5.2 Grain Storage — Azure Blob vs. ADO.NET vs. Redis vs. Cosmos DB

This is the subsystem that actually sees case/grain-scale load (§3's ~500k+ grain activations),
so it's the one worth taking seriously.

| Dimension | Azure Blob (current) | ADO.NET / SQL Server | Redis | Cosmos DB |
|---|---|---|---|---|
| Throughput ceiling | Each journaled grain gets its **own blob** within the shared container (`CaseStateContainerFactory`, `Program.cs:216-229`), so traffic spreads across the **account-level** ceiling (20,000–40,000 req/s, region-dependent — [Standard storage account scalability, Microsoft Learn](https://learn.microsoft.com/en-us/azure/storage/common/scalability-targets-standard-account) **[E]**), not a punishing per-key ceiling. The commonly-cited ~500 req/s target is *per blob*, so it's a non-issue when each grain owns its blob **[E+R]** | Bound by connection-pool + row/page lock contention, not a documented storage-service ceiling — the real risk is the .NET default max pool size of 100 connections per client instance saturating under an activation storm **[E, general ADO.NET pooling]** **[R, Orleans-specific interaction]** | Highest raw throughput of the four (in-memory) **[R]** | RU-metered per operation; no fixed "ops/sec" ceiling, cost scales linearly with request volume instead (see cost row) |
| Latency p50/p99 | Typical in-region Blob HTTP+auth round trip — no authoritative Case.Flow-specific figure found; would need the harness in §7 **[R]** | Comparable or lower for a warm in-VNet connection, at the cost of connection-pool pressure under concurrency **[R]** | Sub-millisecond in the common case **[R]** | Comparable to Blob; consistency-level dependent (see below) |
| Scalability ceiling | Effectively the storage-account ceiling above; can be raised further by support request **[E]** | Lock contention and pool exhaustion under the exact "activation storm" pattern `docs/06` §3.3 already flags as a scale risk **[R]** | None from Redis itself; ceiling is the durability trade-off (below) | RU/s provisioned ceiling; can autoscale but costs more at peak (below) |
| Operational complexity | Already the default; zero new infra | New SQL resource, schema/migration story, connection management | New cache resource, persistence must be explicit | New Cosmos account, container/partition-key design, RU capacity planning |
| Cost at target scale | Already-provisioned, storage-cost-only | Azure SQL compute spend, roughly flat regardless of traffic shape | Persistence-tier cache spend | **Most expensive candidate**: writes cost materially more than reads (a 1 KB point read ≈ 1 RU; the provisioned-throughput pricing model implies writes run ~5× a read's RU cost — [Request Units, Microsoft Learn](https://learn.microsoft.com/en-us/azure/cosmos-db/request-units) **[E]**), and provisioned/autoscale billing is against **peak RU/s per hour**, not actual usage — a poor fit for CMMN's sustained (not bursty) 10k-concurrent-case load, where serverless's pay-per-use advantage doesn't apply **[E+R]** |
| Consistency / transactions | Per-grain optimistic concurrency (ETag-based); no cross-grain transactions — matches Case.Flow's grain-owns-its-state model | Real ACID transactions + secondary-index queryability across grains — a capability Blob/Table structurally can't offer **[E]** | Per-key atomicity only, no cross-key transactions | Configurable consistency levels; strong/bounded-staleness **doubles** the RU cost of every read **[E]** |
| Failure & recovery | Already validated (`AzuriteJournalStorageTests` — restart-survival suite, work item #54) | Would need new coverage | Would need new coverage + explicit AOF/RDB validation given the durability stakes for an audit-trail engine | Would need new coverage |

**The one dimension that could genuinely flip this: query needs.** Nothing in Case.Flow's
current design needs cross-grain transactions or queryability — each grain owns its own
journaled blob and the event-sourced `Apply` projection is the source of truth. But `docs/07`'s
M2 API layer (definitions CRUD, case lifecycle, task inbox, "case-file ops, event/history
queries") could plausibly introduce a real need to query *across* cases/grains by attribute,
which Blob/Table cannot do natively and SQL can. That's the actual trigger condition for
revisiting this — not raw throughput.

**Durability posture matters here more than the other subsystems**, because Case.Flow's stated
differentiator is an immutable, event-sourced audit trail (`docs/06` §3.2 flags `BaseEvent.Occurred`
timestamp-on-replay as *"directly undermin[ing] the business case's 'immutable audit trail'
differentiator"* already — this is not a place to introduce a second, opt-in durability
trade-off). Redis's default posture — RDB loses everything since the last snapshot on crash,
AOF is durable but adds write-path cost, hybrid is the safe-but-not-default configuration
([Redis persistence, redis.io](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/)
**[E]**) — is a bad fit unless deliberately hardened and paid for at Premium tier.

**Recommendation: keep Azure Blob as the default.** ADO.NET/SQL is the only candidate worth an
empirical head-to-head (§7) — gated on whether M2's API layer actually needs cross-grain
queries, not on a throughput concern that isn't evidenced today. Redis and Cosmos are
reasoned-out on durability-fit and cost grounds respectively; neither warrants harness time
before those objections are addressed.

### 5.3 Reminders — Azure Table vs. ADO.NET

| Dimension | Azure Table (current) | ADO.NET / SQL Server |
|---|---|---|
| Throughput/scale | Low-volume workload (registration/cancellation writes + periodic due-tick scans) — nowhere near either backend's ceiling **[R]** | Same |
| Operational complexity | Reuses the exact same `TableServiceClient` already backing clustering — genuinely "one less resource," not just a slogan (`Program.cs:277-285`) | New resource unless already adopted for grain storage (§5.2) |
| Consistency | Reminder rows are independent per grain; no contention concern observed | Same |

**The real decision here isn't the provider — it's whether Orleans Reminders is even the right
timer subsystem.** `docs/07` M2.2 frames the choice as *"Orleans Reminders (or clustered
AdoJobStore Quartz)"* to fix B2/B3 (`docs/06` §3.1: Quartz `RAMJobStore` losing all timers on
silo crash; a singleton scheduler grain as a SPOF). Orleans Reminders carry a **hard, Orleans-wide
1-minute minimum interval** — not a provider limitation, a runtime constraint
([Timers and reminders, Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/orleans/grains/timers-and-reminders)
**[E]**; confirmed as intentional/performance-related in
[dotnet/orleans#4218](https://github.com/dotnet/orleans/issues/4218) **[E]**). CMMN timer
events (`TimerEventListenerBehavior`) plausibly model business-scale delays (hours/days), so
this floor is very likely fine — but that's an assumption about Case.Flow's actual CMMN models,
not a verified fact, and it should be confirmed before the M2 decision is locked in. That
decision (Reminders vs. Quartz-ADO) is bigger than "which storage provider" and should be made
first.

**Recommendation: keep Azure Table for reminders** conditional on Orleans Reminders remaining
the chosen durable-timer mechanism at all. ADO.NET only becomes attractive as a side effect of
adopting SQL for grain storage (shared infra), not on its own merits.

### 5.4 Streams — Azure Queue vs. Event Hubs

Restating §2: this is "what replaces memory streams," not a swap between two durable options.

| Dimension | Azure Queue Storage | Event Hubs |
|---|---|---|
| Throughput | 2,000 msgs/sec/queue, 20,000/sec/account (1 KB messages) — [Queue storage scalability targets, Microsoft Learn](https://learn.microsoft.com/en-us/azure/storage/queues/scalability-targets) **[E]** | 1 MB/s ingress + 2 MB/s egress per throughput unit, up to 40 TUs on Standard before Premium/Dedicated tiers are needed — [Event Hubs scalability, Microsoft Learn](https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-scalability) **[E]** — far higher ceiling |
| Replay | **Not rewindable** — only latest-enqueued consumption **[E]** | **Rewindable** — can replay from an arbitrary point in time up to retention **[E]** |
| Operational complexity | Same storage account, no new resource | New resource type, new SDK, partitioning/consumer-group design |
| Cost | Already-provisioned account cost only | Throughput units billed continuously regardless of load |
| Fit for Case.Flow's actual need | CMMN's internal signaling (plan-item transition events → sentry subscriptions) is consume-once, no spec-identified need to replay a stream | Replay/audit is already handled by the event-sourced grain journal (the `Apply`/journal pattern), so Event Hubs' signature strength is redundant here |

**Recommendation: Azure Queue Storage.** It matches the account default, directly fixes the
fire-and-forget correctness gap `docs/06` already flags, and nothing about CMMN's internal
signaling needs Event Hubs' replay or throughput headroom. Event Hubs would be justified if
Case.Flow ever needs genuine external-eventing replay — but that's explicitly work item #56's
job (the *external* sink), which this spike's brief deliberately keeps separate from the
internal plane.

---

## 6. Should the consistency-first default harden or change?

**Harden it, with one documented pressure-release valve.** Nothing found in this research
contradicts "one Azure Storage account" as the right default at Case.Flow's ~10k-case target
scale:

- Clustering and reminders are provably insensitive to backend choice at Case.Flow's silo
  count (§4) — switching either would add infrastructure and cost for no measurable benefit.
- Grain storage's Blob-per-grain design already sidesteps the one Azure Storage limit
  (per-partition throttling) that would actually bite at scale.
- The two subsystems worth fixing — durable PubSubStore and durable streams — aren't a
  provider-choice question at all; they're an *implementation-gap* question (§2), and Azure
  Queue Storage is the natural, already-in-the-account answer for streams.

The one place to keep a genuine decision open is **grain storage vs. ADO.NET/SQL**, gated
explicitly on whether M2's API layer introduces cross-grain query/transaction needs that
Blob/Table structurally cannot serve. That's a product-shape question as much as a performance
one, and it's the one comparison in this memo worth actually running through the harness in §7
before #30/#35 close out.

---

## 7. Proposed benchmark methodology (Testcontainers harness design — not run in this spike)

The methodology extends the exact pattern work item #60 already established, rather than
inventing a new one. `Flow.Grains.Tests.Integration.Storage.AzuriteClusterFixture` already shows
the shape: a self-contained `IAsyncLifetime` fixture that provisions a throwaway container per
test run (fresh GUID-suffixed resource names, no fixed ports, no shared state), builds a real
`TestCluster` via a swappable `ISiloConfigurator`, and is gated by the process-cached
`DockerAvailability` / `RequiresDockerFact` pair (so CI/dev machines without Docker skip
cleanly instead of failing loud).

**Design, one paragraph:** add one sibling fixture per challenger provider combination —
`MsSqlClusterFixture` (using `Testcontainers.MsSql` against a `mcr.microsoft.com/mssql/server`
image, schema bootstrapped from Orleans' shipped ADO.NET provider scripts) and
`RedisClusterFixture` (using `Testcontainers.Redis`, with AOF-everysec persistence explicitly
enabled so the comparison is apples-to-apples on durability, not just raw throughput) —
each mirroring `AzuriteClusterFixture`'s constructor/`InitializeAsync`/`DisposeAsync` shape
and swapping only the provider-specific `ISiloConfigurator` block (clustering/storage/reminders
registrations), exactly as `AzuriteClusterFixture`'s own comment already documents doing versus
`ClusterFixture`'s in-memory baseline. Cosmos DB is deliberately **excluded** from the reusable
local harness — there is no Testcontainers-parity local emulator for Cosmos as clean as
Azurite/mssql/redis (the Linux Cosmos emulator container is heavy and known-flaky), so a Cosmos
comparison would have to run against a real (throwaway) Cosmos account and is out of scope for
a repeatable local/CI harness; it's noted here rather than silently dropped. A synthetic
workload driver — issuing the #35 target shape (10,000 concurrent cases × 50 plan items) through
`Flow.Application`'s CQRS command surface (the native-mediator `AddFlowApplication` entry point
from work item #32, already the intended app-facing seam) against whichever fixture is under
test — would record: sustained throughput before the provider starts returning 503/throttle
responses, p50/p99 command latency, error/retry counts, and (for the SQL challenger
specifically) connection-pool high-water-mark, since that's the mechanism §5.2 flags as the
plausible SQL bottleneck under Case.Flow's activation-storm shape. Each provider combination
would run as an isolated `TestCluster`, so results are directly comparable without cross-run
interference.

This design is **not implemented or run** as part of this spike — there is no Docker/infra
available in this research environment, and the brief explicitly scoped this spike to design,
not execute, the empirical validation.

---

## 8. Evidence ledger

**Evidence-backed [E]** (sourced, verifiable):
- Azure Table Storage: 2,000 entities/sec/partition, 20,000 tx/sec/account, 1 MiB max entity —
  [Table storage scalability targets](https://learn.microsoft.com/en-us/azure/storage/tables/scalability-targets)
- Standard GPv2 storage account: 20,000–40,000 req/sec account-wide (region-dependent), 25–60 Gbps
  ingress, 50–200 Gbps egress —
  [Standard storage account scalability targets](https://learn.microsoft.com/en-us/azure/storage/common/scalability-targets-standard-account)
- Orleans membership protocol mechanics, version-row serialization, "200 silos proven / challenges
  beyond a thousand," direct P2P probing bypassing the table, Redis durability requirement —
  [Cluster management in Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/cluster-management)
- Azure Queue Storage: 2,000 msgs/sec/queue, 20,000/sec/account, not rewindable —
  [Queue storage scalability targets](https://learn.microsoft.com/en-us/azure/storage/queues/scalability-targets)
- Event Hubs: 1 MB/s ingress + 2 MB/s egress per TU, up to 40 TUs Standard, rewindable —
  [Event Hubs scalability guide](https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-scalability)
- Cosmos DB RU cost model, provisioned/autoscale billed on peak RU/s per hour, strong/bounded-staleness
  doubling read RU cost —
  [Request Units as a throughput and performance currency](https://learn.microsoft.com/en-us/azure/cosmos-db/request-units)
- Redis RDB vs. AOF durability trade-offs —
  [Redis persistence](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/)
- Orleans reminders' 1-minute minimum interval as an intentional, performance-related, runtime-wide
  constraint — [Timers and reminders](https://learn.microsoft.com/en-us/dotnet/orleans/grains/timers-and-reminders),
  [dotnet/orleans#4218](https://github.com/dotnet/orleans/issues/4218)
- ADO.NET default connection pool max size (100) as the generic saturation mechanism —
  general .NET/SQL Server ADO.NET documentation (not Orleans-specific)
- Current Case.Flow provider wiring (clustering, storage, reminders, streams, PubSubStore) —
  direct read of `src/Flow.Silo/Program.cs` and `src/Flow.Silo/Infrastructure/Options/AzureOptions.cs`
  in this repository

**Reasoned, not independently verified [R]**:
- Any specific latency (p50/p99) figure for any provider under Case.Flow's actual workload —
  no benchmark was run; §7 is the design to close this gap
- SQL Server connection-pool/lock contention actually becoming the bottleneck under Case.Flow's
  specific activation-storm pattern (plausible given `docs/06` §3.3's existing flag, not measured)
- Whether Case.Flow's real CMMN models tolerate a 1-minute reminder floor (depends on modeling
  practice, not verified against actual timer-bearing plan items)
- Cost-at-scale comparisons beyond the qualitative "which pricing model fits a sustained vs.
  bursty load" reasoning — no dollar figures were computed against a concrete deployment SKU

---

## 9. What this spike does NOT resolve

- No empirical numbers exist for any provider against Case.Flow's actual workload — §7's harness
  is designed, not run.
- Whether M2's API layer will need cross-grain query/transaction capability (the one condition
  that would make ADO.NET/SQL grain storage worth switching to) is a product-roadmap question,
  not something this technical spike can answer.
- Whether Orleans Reminders' 1-minute floor is acceptable for Case.Flow's actual CMMN timer
  usage needs a look at real/planned models, not just the spec.
