# Case-Grain Granularity Redesign — Design

**Status:** draft for review. No implementation issues have been cut.
**Decision being designed:** D-2026-08-01 (see [04 §9](04-adversarial-review-2026-08.md)). The decision itself is not re-litigated here.
**Base:** `github/develop` @ `4751ff2`, plus PR #236's review document.
**Audience:** the implementer who will build this, and the reviewer whose job is to find the holes.

> **Spec citations.** All CMMN citations are to OMG CMMN 1.1 (`formal-16-12-01`), by **printed** page. The PDF is not in this repo. **Printed page = PDF page − 18.** Every normative claim in §C and §D below was read from the PDF directly, not from [03](03-cmmn-execution-semantics.md). Where this document disagrees with [03](03-cmmn-execution-semantics.md), the PDF wins and the disagreement is called out.

---

## 0. What this document decides, and what it leaves open

Twelve open questions are recorded inline and collected in [§H](#h-open-questions-collected). They are deliberately unresolved: each is a place where CMMN is silent, the repo's evidence is incomplete, or two defensible readings exist. Picking silently now converts each into an implementation surprise later.

Three things this design asserts that the review did **not** find, and that reviewers should attack first:

1. **`R<n>` is currently off by one** (§C.7a). `Iso8601.Repetitions` is passed straight to Quartz's `WithRepeatCount`, which means "n fires *after* the first". `R2/PT1H` therefore produces three occurrences, not two. This is a new finding, adjacent to M7b but distinct from it.
2. **`BaseBehavior.EvaluateRule` journals an audit event even when the model configures no rule** (`BaseBehavior.cs:347-352` — the `rule?.Condition != null` check guards only the *expression execution*). Three of the ~8 journal events every plan item costs are therefore pure noise. This is the single cheapest lever on journal volume (§D.3).
3. **Two things [04 §10](04-adversarial-review-2026-08.md) records as worth preserving cannot survive as-is** — the #198 blocking/clearing-signal design, and `CmmnElementGrain` as a shared abstraction. Both are deleted, not ported. See [§G.3](#g3-what-cannot-survive-findings).

---

## A. Target architecture

### A.1 The unit of consistency

One `JournaledGrain` per **case instance**. Everything inside a case — the CasePlanModel, every plan item instance, every sentry instance, every case-file item, the planning tables and roles — is a plain CLR object owned by that grain's activation. There are **no Orleans streams inside a case**, no PubSub subscriptions, no intra-case grain calls, and no `IAsyncStream` of any kind.

```
                    ┌──────────────────────────────────────────────────┐
                    │  CaseGrain  (JournaledGrain, NOT [Reentrant])    │
   HTTP / ISender   │                                                  │
   ───────────────▶ │  ┌────────────┐   drains    ┌────────────────┐   │
                    │  │  Command   │────────────▶│  Agenda loop   │   │
                    │  │  preamble  │             │   (§B)         │   │
                    │  └────────────┘             └────────┬───────┘   │
                    │                                      │           │
                    │  ┌───────────────────────────────────▼────────┐  │
                    │  │ CaseRuntime (in-memory object graph)       │  │
                    │  │   CasePlanModel instance                   │  │
                    │  │     └─ PlanItemInstance tree               │  │
                    │  │          └─ CriterionInstance              │  │
                    │  │               └─ SentryInstance            │  │
                    │  │   CaseFileItemInstance map                 │  │
                    │  │   PlanningTableInstance / RoleInstance     │  │
                    │  │   ArmedTimer set                           │  │
                    │  └────────────────┬───────────────────────────┘  │
                    │                   │ one ConfirmEvents per turn   │
                    │  ┌────────────────▼───────────────────────────┐  │
                    │  │ Case journal (totally ordered, append-only)│  │
                    │  └────────────────────────────────────────────┘  │
                    └──────────┬─────────────────────────┬─────────────┘
                               │ bound request           │ RegisterOrUpdateReminder
                               ▼                         ▼
                    ┌──────────────────────┐   ┌──────────────────────┐
                    │ ExpressionGrain      │   │ Orleans reminders    │
                    │ [StatelessWorker]    │   │ (durable, tenant-free│
                    │ NO outbound calls    │   │  entry point → §D.5) │
                    └──────────────────────┘   └──────────────────────┘

                    ┌──────────────────────┐
                    │ CaseDefinitionGrain  │  resolved ONCE at Create,
                    │ (tenant-keyed)       │  pinned into the case journal
                    └──────────────────────┘
```

### A.2 The internal object model

```csharp
// Not a grain. One per case activation, rebuilt by folding the journal.
internal sealed class CaseRuntime
{
    Guid                                    CaseInstanceId;
    Guid                                    TenantId;          // pinned at Create; never read from RequestContext
    string                                  CaseDefinitionId;
    Case                                    Definition;        // pinned at Create (§A.5)

    PlanItemInstance                        Root;              // the CasePlanModel instance
    Dictionary<string, PlanItemInstance>    InstancesById;     // instance id -> instance
    Dictionary<string, CaseFileItemInstance> CaseFile;         // CaseFileItem definition id -> item
    Dictionary<string, RoleInstance>        Roles;
    Dictionary<string, ArmedTimer>          Timers;            // timer instance id -> schedule state

    long                                    NextInstanceOrdinal;
    long                                    NextEventOrdinal;  // == journal position; see §C.8
}

internal sealed class PlanItemInstance
{
    string              InstanceId;          // §A.3
    int                 Repetition;          // 0 for the first instance
    PlanItem            Item;                // the <planItem> model element
    PlanItemDefinition  Definition;          // the resolved definitionRef target
    PlanItemInstance    Parent;              // null only for the CasePlanModel root
    List<PlanItemInstance> Children;         // Stage / CasePlanModel only

    PlanItemState       State;
    PlanItemState?      ParentSuspendState;  // Figure 8.3's history pseudo-state (printed p.113)
    bool                Required;            // memoized RequiredRule (§C.1)
    bool?               ManualActivation;    // memoized ManualActivationRule
    bool                Repeatable;          // memoized RepetitionRule (first evaluation discarded)
    bool                UserCompletable;     // Table 8.12 manual-completion latch

    List<CriterionInstance> EntryCriteria;
    List<CriterionInstance> ExitCriteria;
    List<DeferredEffect>    PendingWhileSuspended;   // §C.6

    IPlanItemBehavior   Behavior;            // StageBehavior / TaskBehavior / … (§G.1)
    PlanItemStateMachine Machine;            // Stateless, configured per Tables 8.6/8.8/8.11
    PlanningTableInstance PlanningTable;     // Stage / HumanTask only
}

internal sealed class CriterionInstance
{
    string          Id;                      // the model Criterion's id
    CriterionKind   Kind;                    // Entry | Exit
    PlanItemInstance Owner;
    SentryInstance  Sentry;                  // one per criterion instance — see §C.8, Decision D-C8-1
}

internal sealed class SentryInstance
{
    Sentry                          Definition;
    PlanItemInstance                OwnerScope;      // the Stage instance that contains the Sentry
    Dictionary<string, Occurrence>  Pending;         // OnPart.Id -> most recent unconsumed occurrence
    List<SatisfactionRecord>        Satisfactions;   // ledger, NOT a monotonic bool (§C.8)
    bool                            LastEvaluationFaulted;
}

internal readonly record struct Occurrence(long EventOrdinal, string SourceInstanceId, object StandardEvent);
```

`CaseFileItemInstance` carries `DefinitionId`, `Definition`, `Value` (`JsonNode`), `State` (Available/Discarded), `Children`, `TargetRefs`, and a **per-item logical version counter** (§D.4.3).

### A.3 Instance identity

Today a plan-item instance is identified by a `ShortGuid` minted by the parent stage (`StageBehavior.cs:1744`) and embedded in a dotted Orleans grain key (`"CPM.<a>.<b>"`), from which `CmmnElementGrain.OnActivateAsync` re-derives scope/parent by string splitting (`CmmnElementGrain.cs:47-56`). None of that survives: there is no grain key to encode a tree into, and the tree is now real object references.

**Design.** Each plan-item instance gets a **per-case monotonic ordinal**, rendered as a short opaque string (`"pi-17"`). It is assigned by the case grain when the instance is created and **carried on the `PlanItemInstantiated` journal event**, so replay reproduces identity exactly without the minting function needing to be deterministic. Properties:

- Stable across activations, deactivations, and replays.
- Sorts by creation order — a useful default ordering for views and for the deterministic tie-break in §B.3.
- Compact in the journal (this matters: §D.3).
- Opaque to callers. The tree structure is exposed as `ParentInstanceId` on the snapshot, not encoded in the id.

A human-readable **path** (`CPM/StageA#0/TaskB#2`, definition ids plus repetition ordinals) is computed on demand for logs, error messages, and the case view. It is *never* an identifier — repetitions make it non-unique only if the repetition ordinal is omitted, and it changes meaning if a model is edited.

Case-file items keep their current identity: the **CaseFileItem definition id**, case-global, no separate instance id. That is unchanged and is what the HTTP surface already exposes.

Sentry instances are identified by `(owning criterion instance id)` — see [Decision D-C8-1](#c8-the-sentry-satisfaction-algebra-c4--m8--m9--m10).

### A.4 What stays a grain

| Grain | Fate | Key |
|---|---|---|
| `ICaseGrain` | **Becomes the whole case.** `JournaledGrain`, non-reentrant, `IRemindable`. | §D.5 |
| `ICaseDefinitionGrain` | **Kept**, unchanged. Immutable deployed definitions. | `(tenantId, caseDefinitionId)` |
| `IPlanItemDefinitionGrain` | **Kept**, unchanged. | `(tenantId, "{caseDefId}.{node}")` |
| `IExpressionGrain` | **Kept** as `[StatelessWorker]`, **contract rewritten** (§D.2). | `caseInstanceId` |
| `ITenantGrain`, `IUserIdentityGrain` | **Kept**, untouched (identity, not case runtime). | unchanged |
| `IPlanItemGrain` / `IPlanItemInternalGrain` | **Deleted.** | — |
| `ICaseFileItemGrain` | **Deleted**; operations move onto `ICaseGrain` (§D.4). | — |
| `ISentryGrain` | **Deleted.** (Has no members of its own today.) | — |
| `IPlanningTableGrain` | **Deleted**; `GetPlannableItems` moves onto `ICaseGrain`. | — |
| `IRoleGrain` | **Deleted**; `Authorize` becomes an in-memory check. | — |
| `ITimerEventSchedulerGrain` | **Deleted** with the whole `Scheduler/` and `Infrastructure/Quartz/` trees (§C.7). | — |

**`IPlanItemGrain` and `ICaseFileItemGrain` as public surfaces.** Both cease to exist as grain interfaces. This is far less disruptive than it sounds, and the evidence is concrete:

- **`IPlanItemGrain` is already nearly dead.** Everything outside `Wayfinder.Grains` resolves `IPlanItemInternalGrain` (which lives *inside* the implementation assembly, and is the only reason `Wayfinder.Application` project-references `Wayfinder.Grains` at all). There are exactly **three production call sites** across both interfaces: `CaseViewProjector.cs:64`, `GetCaseFileItemHistoryQueryHandler.cs:36`, `GetCaseFileItemValueAtQueryHandler.cs:35`.
- **No HTTP route addresses a plan-item instance.** `PlanItemView` carries only the *definition* id; the only trigger endpoint targets the case root. Collapsing plan items breaks **zero** HTTP contract.
- The case-file HTTP routes (`/case-file-items({itemId})/history`, `/versions({itemVersion})`) *are* real contracts and are preserved — see §D.4.3, which is the one genuinely hard compatibility problem in this redesign.

The blast radius is therefore almost entirely test code: ~67 `Mock<IPlanItemInternalGrain>` setups across six `StageBehaviorTests_*` files, and ~110 integration call sites across 23 files. §D.4.4 specifies how they adapt.

### A.5 Definition pinning

At `Create`, the case grain resolves the **entire** definition graph from `ICaseDefinitionGrain` once and records it in the journal (`CaseCreated` already carries the full `Case`; this extends it to the resolved `PlanItemDefinition` for every `<planItem>`, so `GetPlanItemDefinition` is never called again).

Three consequences, all improvements:

1. **Nothing in the agenda loop calls the definition grain.** Child instantiation becomes a pure in-memory lookup. This removes an entire class of outbound call from the loop (§D.6).
2. **A redeployed definition cannot change a running case.** Today `PlanItemGrain.DefineRepetition` re-reads the definition on every repetition spawn, so a redeploy between spawns silently changes a live case's model. That is not a finding in [04](04-adversarial-review-2026-08.md); it is closed here as a side effect.
3. It costs journal size. Bounded by one copy of the model per case — measured in §D.3.

---

## B. The agenda loop

### B.1 Shape of a turn

Every public mutating method on `ICaseGrain` has the same shape:

```
1. Authorize      — read CaseRequestContext exactly once; compare to CaseRuntime.TenantId (§D.5)
2. Admit          — allowlist check (§C.5); idempotency check (§D.6.3)
3. Seed           — journal CommandReceived; push the initiating operation(s) onto the agenda
4. Drain          — run the agenda to fixpoint (§B.2)
5. Confirm        — ONE ConfirmEvents() for the whole turn
6. Reconcile      — register/unregister Orleans reminders to match the confirmed state (§C.7 / §D.1)
7. Project        — return the post-fixpoint CaseSnapshot
```

Step 5 is the atomicity claim of this entire redesign: **one command, one fixpoint, one journal write.** A crash anywhere in steps 3–4 loses the whole turn and leaves the case exactly as it was. That single property closes D1 (no outbox needed — there is nothing to publish), D2 (no orphan children — the parent's bookkeeping and the child's existence are one write), and the #161/#177/#181 redelivery lineage (there is no delivery).

Step 6 is deliberately *after* the confirm and is **idempotent and self-healing**: see §D.1.4.

### B.2 The agenda

Two structures, not one:

- **`fifo`** — a FIFO queue of work that must happen in the order it was determined.
- **`deferred`** — a *set* of stage instances awaiting a Table 8.12 completion evaluation. A set, so enqueueing the same stage twice is free.

```
drain():
    while fifo.Count > 0 or deferred.Count > 0:
        if fifo.Count > 0:
            op = fifo.Dequeue()
        else:
            op = EvaluateCompletion(deferred.RemoveDeepestThenLowestOrdinal())
        execute(op)          // may push onto fifo and/or deferred
```

Completion checks run only when nothing else is pending. That ordering is not a heuristic — it is the structural replacement for #198's blocking/clearing-signal gate. A stage cannot complete over a determined-but-uncreated successor, because the instantiation operation is still on the FIFO and the FIFO drains first. The gate, `RepetitionRequestsAwaitingResolution`, `SettleRepetitionRequest`, `RepetitionRequestSettled`, and `TryCompleteStageAfterRepetitionResolved` are all **deleted** (§G.3).

**Deterministic order within `deferred`:** deepest stage first (by tree depth), ties broken by ascending instance ordinal. Deepest-first because an inner stage completing is itself a child state change on its parent, which re-enqueues the parent; the reverse order does the same work twice.

### B.3 Operation types

Six, and only six. Every operation is a two-phase object:

```csharp
internal interface IAgendaOperation
{
    // May await IExpressionGrain. Must not mutate CaseRuntime and must not raise journal events.
    ValueTask<TPrepared> PrepareAsync(CaseRuntime rt);

    // Pure, synchronous. Mutates CaseRuntime, raises journal events, enqueues successors.
    void Apply(CaseRuntime rt, TPrepared prepared, IAgenda agenda);
}
```

| # | Operation | Prepare may evaluate | Apply does |
|---|---|---|---|
| 1 | `Transition(instance, trigger, exitCriterionRef?)` | — | Fires the Stateless machine; journals `Transitioned`; enqueues cascades (§C.4), sentry evaluations for every OnPart matching this transition, and `EvaluateCompletion(parent)` |
| 2 | `RecordAndEvaluateSentry(sentry, onPart, occurrence)` | IfPart | Records the occurrence and evaluates satisfaction **atomically** (§C.8); on satisfaction enqueues `ConsumeSatisfaction` |
| 3 | `ConsumeSatisfaction(criterion, satisfaction)` | ManualActivationRule, RepetitionRule | Applies §8.5's criterion semantics to the owning instance: enable/start, exit, occur, or spawn a repetition; or defers if suspended (§C.6) |
| 4 | `Instantiate(stage, planItemModel, repetition)` | RequiredRule, RepetitionRule (discarded on first) | Creates the instance, arms its criteria, fires `create`; enqueues `EvaluateCompletion(stage)` |
| 5 | `EvaluateCompletion(stage)` | RequiredRule is memoized — no evaluation | Table 8.12 (§C.2); may enqueue `Transition(stage, complete)` |
| 6 | `PropagateContainment(instance)` | — | Applies Table 8.9's containment invariant after a transition that could violate it (§C.4) |

**The two-phase split is load-bearing.** Rule and IfPart evaluation requires an outbound call to `ExpressionGrain`. Confining every such call to `Prepare` — never inside a Stateless entry/exit action — buys three things:

- Every await sits at an operation boundary. `CaseRuntime` is never torn across an await.
- Stateless transitions become **fully synchronous**. `PlanItemStateMachine.RetainSynchronizationContext = true` and the `FiringMode.Queued` reentrancy gymnastics (the 55-line comment at `PlanItemStateMachine.cs:36-89`, closing #64) exist *only* because `HandleEnterAvailableFromCreate` awaits an `IExpressionGrain` call from inside an entry action. That whole hazard class disappears.
- The termination argument in §B.4 has a clean unit of accounting.

### B.4 Termination

"It drains" is not a design. Here is the argument, its limits, and the backstops.

#### B.4.1 The invariant that makes the argument possible

> **I-1 (progress).** An operation may enqueue successor operations **only if its `Apply` appended at least one journal event.**

This is enforceable: `Apply` receives the journal position before and after; the agenda rejects an enqueue from an operation that appended nothing. Under I-1, non-termination requires the journal to grow without bound *within a single turn*.

> **I-2 (purity).** Expression evaluation is side-effect free. Jint is sandboxed (`SandboxedJintEngine`), receives a bound `JsonNode` copy, and has no write path back into the case file.

I-2 is currently true and must stay true. If expressions ever gain the ability to mutate the case file, the argument below collapses entirely and this section must be rewritten before that ships.

#### B.4.2 The monotonicity theorem

Define the lifecycle rank of a Stage/Task instance:

```
Uninitialized(0) < Available(1) < Enabled(2) < Active(3) < {Disabled, Failed}(4) < {Completed, Terminated}(5)
```

and for Milestone/EventListener: `Uninitialized(0) < Available(1) < {Completed, Terminated}(5)`.

**Claim.** Every transition a *sentry* can cause is rank-increasing.

**Proof by enumeration** of the transitions reachable from a satisfied sentry, per Table 8.8 (printed pp.115-117) and Table 8.11 (printed pp.120-121):

| Transition | From → To | Rank | Driven by |
|---|---|---|---|
| `enable` | Available → Enabled | 1→2 ↑ | entry criterion + ManualActivationRule TRUE |
| `start` | Available → Active | 1→3 ↑ | entry criterion + ManualActivationRule FALSE |
| `occur` | Available → Completed | 1→5 ↑ | achieving sentry (Milestone) |
| `exit` | {Available, Enabled, Disabled, Active, Suspended, Failed} → Terminated | ≤4→5 ↑ | exit criterion |
| `terminate` | Active → Terminated | 3→5 ↑ | CasePlanModel exit criterion |

The transitions that *decrease* rank — `disable` (2→4 is an increase; but `re-enable` 4→2 decreases), `resume`, `parent resume`, `re-activate` — are, per Table 8.6/8.8/8.11's own Description column, **exclusively** Case-worker, administrator, or parent-propagation transitions. No sentry can fire any of them. (Table 8.8 `re-enable`: "Transition by a Case worker (human) decision." Table 8.8 `resume`: "Transition by Case worker (human) decision or propagation from outer Stage instance." Table 8.8 `re-activated`: "Transition by the systems, an administrator, or by Case worker (human)…".)

Parent propagation (`parent resume`, and the `re-enable`/`manual start` a bottom-up activation would fire) *can* decrease rank, but only as part of a **single, externally-initiated cascade** that visits each instance at most once. Cascades are tree walks guarded by `CanFire`, and §C.11 explicitly forbids the one place today's code auto-fires a rank-decreasing transition without a human (`HandleChildTransitioned`'s `Reenable` arm at `StageBehavior.cs:578-581`).

**Consequence.** Within one turn, after the initiating command's cascade has been applied, the multiset of instance ranks is non-decreasing and bounded above by 5·|instances|. Sentry occurrences are bounded by the number of transitions raised. Completion checks are deduplicated by the `deferred` set and are enqueued only by state changes. So the loop terminates — **provided |instances| is bounded.**

#### B.4.3 Where the argument does not hold, stated plainly

|instances| is **not** bounded by the model. Repetition creates instances at runtime. Three sub-cases:

- **Entry-criterion repetition** (§8.6.4, printed p.122). A new instance per satisfaction of an entry criterion carrying an OnPart. Bounded by the number of source occurrences, which is bounded by the rank argument — *unless* the source is itself repeating, in which case B spawning B′ spawning B″ is exactly the spec's Example 1 (printed p.123) and is intended. Bounded in practice by whatever ends the chain.
- **No-entry-criteria repetition** (Table 8.8 `complete`/`terminate` rows, printed p.116). An instance that completes re-evaluates the RepetitionRule and creates a successor. If the successor is non-blocking, it completes inside the same turn and repeats. **This is a genuine unbounded loop** whenever the RepetitionRule is a constant `true`. It is not a hypothetical: `RepetitionGuardFootgunIntegrationTests` exists precisely because of it.
- **Timer repetition.** Not a source: each tick is a separate reminder turn (§C.7).

#### B.4.4 The bound and the detector

Two backstops, both required:

**1. Per-plan-item repetition ceiling — retained.** `RepetitionGuardOptions.MaxRepetitionsPerPlanItem` (#67) carries over unchanged, now enforced synchronously inside the loop rather than across a stream hop. On breach: journal `RepetitionCeilingExceeded`, fire `fault` on the containing CasePlanModel (today's behavior, pinned by `RepetitionGuardFootgunIntegrationTests`).

**2. Divergence detection — new.** Per turn, the agenda maintains:

- a **signature multiset** keyed by `(operationKind, targetInstanceId, trigger)`. A signature recurring more than `MaxSignatureRepeats` (default 3) times in one turn is a cycle. This gives a *named culprit*, which a bare counter cannot.
- an **absolute operation budget** `MaxOperationsPerTurn` (default 10 000). This is the backstop for a divergence that never repeats a signature — e.g. an unbounded chain of *distinct* instances.

On either trip, the loop stops and:

- journals `AgendaDivergenceDetected { reason, culpritSignature?, operationCount, lastNOperations }`,
- fires `fault` on the CasePlanModel (Table 8.8 `fault`: "Transition when an exception or software failure occurs. This state MUST NOT propagate." — printed p.115, and `ConfigureForCasePlanModel` already permits Active→Failed),
- confirms the journal,
- returns the snapshot (the caller sees a `Failed` case, not an exception).

**Why fault rather than abort.** Aborting the turn (throwing, never confirming) leaves no record of *what* diverged, which makes the bug undiagnosable. Faulting is the spec's own failure state, is auditable, and is recoverable by the spec's own `re-activate` — which Table 8.6 (printed p.113) grants to "a Case worker (human), or an administrator".

> **Open question OQ-1.** The cost of faulting is that a divergent cascade's partial effects become durable. An operator who re-activates a case that diverged resumes from a state the model never intended. The alternative — abort without confirming, and journal the divergence into a *separate* diagnostics sink — keeps the case clean but needs a second write target. Recommendation: fault. Reviewers should push on whether "durable partial cascade" is acceptable.

> **Open question OQ-2.** `MaxOperationsPerTurn = 10 000` is a guess. It must satisfy `budget × per-operation-cost < response timeout` (§D.6.2), and the per-operation cost is dominated by expression evaluation, which is unmeasured. `Wayfinder.Benchmarks` exists and should be pointed at this before the constant is fixed.

### B.5 Journal semantics

#### B.5.1 Commands vs. transitions — **both**, at different levels

| Event class | Purpose | Replayed? |
|---|---|---|
| `CommandReceived { commandId, actor, tenant, method, arguments, correlationId }` | Audit ("who asked for what") and idempotency (§D.6.3) | **No** |
| Effect events — `Transitioned`, `PlanItemInstantiated`, `SentryOccurrenceRecorded`, `SentrySatisfied`, `RuleEvaluated`, `CaseFileItemValueChanged`, `TimerArmed`, `TimerFired`, `EffectDeferred`, … | Reconstruct state | **Yes** |
| `TurnCompleted { commandId, operationCount, journalPositionRange }` | Marks the fixpoint boundary; makes "what did this one command cause" answerable in the audit trail | **No** (fold ignores it) |

**Effect events are authoritative for replay; commands are never re-executed.** This is not a stylistic choice. `JournaledGrain` replay is a pure fold — entry actions do not re-run. Re-deriving state by replaying *commands* would require re-running the agenda loop, which is only sound if the loop is deterministic, and it is not: expression evaluation goes through Jint, and timer arithmetic reads the clock. Recording commands only would reproduce the D1 defect in a new form.

#### B.5.2 What a replay must reconstruct

Exhaustively, because anything omitted here is a silent post-restart bug:

1. Every plan-item instance: id, repetition ordinal, `PlanItem`/`PlanItemDefinition` binding, parent link, creation order.
2. Every instance's `State` **and** `ParentSuspendState` (Figure 8.3's history pseudo-state, printed p.113; Table 8.9 Note (2), printed p.119). *This is where M3 lives today* — `PlanItemStore.Apply(ParentSuspended)` records the state at apply time rather than the transition's source. In the redesign, `Transitioned` carries `Source`, `Destination`, and `Trigger`, and `ParentSuspendState` is derived from `Source` during the fold. M3 is closed by construction.
3. Every memoized rule value: `Required`, `ManualActivation`, `Repeatable`, plus each rule's last evaluation error.
4. Every sentry instance's `Pending` map (per-OnPart occurrence with its event ordinal) and satisfaction ledger.
5. Every deferred effect held under suspension (§C.6).
6. Every case-file item's value, lifecycle state, children/targetRefs, and per-item logical version.
7. Every armed timer: anchor instant, parsed schedule, remaining occurrence count, next fire instant, and the reminder name it maps to.
8. The `UserCompletable` latch per stage.
9. `NextInstanceOrdinal` and `NextEventOrdinal`.

**Not** reconstructed, deliberately: the agenda. It is empty at every confirm boundary, by construction — that is what makes the turn atomic. Assert it: `Debug.Assert(fifo.Count == 0 && deferred.Count == 0)` immediately before `ConfirmEvents()`.

---

## C. The §9 "must be designed INTO the loop" list

### C.1 M1 — RequiredRule re-evaluation on `enable` / `re-enable`

**Spec, verbatim.** Table 8.8, `enable` (Available → Enabled), printed p.115:

> "…If the `RequiredRule` Boolean expression exists and the current value is FALSE, then it MUST be re-evaluated in this transition and its Boolean value SHOULD be maintained for the rest of the life of the `Stage` or `Task` instance."

Table 8.8, `re-enable` (Disabled → Enabled), printed p.116, carries the **identical sentence**. Table 8.8 `create` (printed p.115) requires the first evaluation. §8.6.3 (printed p.122) gives the default: "If this rule is not present, then it is considered FALSE."

**Design.**

```
on Transition(instance, enable | reenable):
    if instance.Item.EffectiveControl.RequiredRule is not null and instance.Required == false:
        Prepare:  result = evaluate(RequiredRule)
        Apply:    journal RequiredRuleEvaluated { result, trigger, error }
                  instance.Required = result          // false -> true only; never true -> false
                  if result changed: agenda.Defer(EvaluateCompletion(instance.Parent))
```

Precise consequences, each of which is a place a naive implementation goes wrong:

- **The guard is `current value is FALSE`, not "always".** Once TRUE, the value is frozen — that is what "SHOULD be maintained for the rest of the life" means, and re-evaluating a TRUE rule to FALSE would let a parent complete over work the model already established as required.
- **Absent rule ⇒ FALSE, never evaluated.** No `RequiredRuleEvaluated` event is raised at all. This differs from today, where `BaseBehavior.EvaluateRule` journals an audit event even for an absent rule (`BaseBehavior.cs:347-352`) — see §D.3.
- **Milestones do not re-evaluate.** Table 8.11 (printed pp.120-121) gives Milestones no `enable` or `re-enable` transition. Their RequiredRule is evaluated once, on `create` (Table 8.11 `create`: "For a `Milestone` instance, the `RequiredRule` Boolean expression MUST be evaluated in this transition…").
- **The completion re-enqueue is belt-and-braces.** A false→true flip can only make completion *harder*, so a missed enqueue cannot wrongly complete a stage. And `re-enable` is itself a child state change (Disabled → Enabled removes the child from Table 8.12's terminal set), which enqueues a completion evaluation anyway under §C.2. Both paths are specified so neither is relied on alone.
- **`enable` is engine-driven** (it is the ManualActivationRule-TRUE branch of criterion consumption), so this evaluation happens inside `ConsumeSatisfaction.Prepare`. `re-enable` is Case-worker-driven and happens inside the command preamble's `Prepare`. Two call sites, one rule.

[03 §7](03-cmmn-execution-semantics.md) states RequiredRule is "evaluated on instantiation into Available" and omits the re-evaluation entirely. That table row must be corrected when this lands.

### C.2 M2 — completion re-evaluation on **every** child state change

**Spec, verbatim.** Table 8.12 (printed p.122):

> | | `autoComplete = TRUE` | `autoComplete = false` |
> |---|---|---|
> | Stage instance completion criteria | There are no Active children, AND all required (`requiredRule` evaluates to TRUE) children are in {Disabled, Completed, Terminated, Failed}. | There are no Active children AND (all children are in {Disabled, Completed, Terminated, Failed} AND there are no `DiscretionaryItems`) OR (Manual Completion AND all required (`requiredRule` evaluates to TRUE) children are in {Disabled, Completed, Terminated, Failed}). |

followed by: "In other words, a `Stage` instance SHOULD complete if a user has no option to do further planning or work with the `Stage` instance."

**The defect being closed.** `StageBehavior.cs:595` gates the completion evaluation on `@event.Destination.IsTerminal()`. The comment above it claims the criteria "can only change their answer when a child reaches one of Table 8.12's OWN states". That is false for the **no-Active-children** conjunct: `suspend` removes a child from the Active count without reaching a terminal state. Result: `autoComplete` outcomes are transition-order dependent.

**Design — and this is the part the agenda model gives naturally, stated precisely.**

The enqueue rule is a *single* rule with no state predicate in it:

> **R-C2.** Any operation whose `Apply` changes (a) a child instance's `State`, (b) a child instance's `Required` memo, (c) the set of child instances of a stage, or (d) the set of plannable `DiscretionaryItem`s of a stage, MUST call `agenda.Defer(EvaluateCompletion(thatStage))`.

Because `deferred` is a **set**, this is idempotent: a cascade that changes fifty children's states enqueues one completion evaluation per affected stage, not fifty. Because `deferred` drains only when the FIFO is empty, that one evaluation sees the *settled* state of the whole cascade rather than an intermediate one. Both properties are why "re-evaluate on every change" is affordable here and was not affordable across streams — this is the concrete content of [04 §9](04-adversarial-review-2026-08.md)'s "the agenda model gives this naturally".

`IsTerminal()` disappears from the enqueue path entirely. It survives only *inside* `EvaluateCompletion`, where it is the literal Table 8.12 predicate.

**The residual semantic question the review flags.** With R-C2, a non-required child that suspends now correctly triggers a completion evaluation. Table 8.12's TRUE column is then satisfied (Suspended is not Active; the child is not required) and the stage completes — but Table 8.9's `complete` row (printed p.119) marks a **Suspended** Stage/Task child under a Completed parent `<impossible>`.

**Decision D-C2-1.** The stage **completes**, and the Table 8.9 cascade (already implemented for #179 at `StageBehavior.cs:525-533`) drives the Suspended non-required child to Terminated via `exit`. Rationale: Table 8.12 *permits* the completion; Table 8.9 then *requires* the cascade; the two reconcile only if completion itself quiesces the remainder — which is exactly the reading #179 already adopted for Available/Enabled children, and exactly what the manual-completion path already does. A **required** Suspended child still blocks, because Suspended ∉ {Disabled, Completed, Terminated, Failed}.

> **Open question OQ-3.** The opposite reading — that `<impossible>` means "do not complete" — is defensible, and [04 §M2](04-adversarial-review-2026-08.md) notes it. It would require a fifth conjunct ("no Suspended children") that Table 8.12 does not state. This is structurally the same tension as [03 §9](03-cmmn-execution-semantics.md)'s born-and-immediately-terminated repetition: the spec permits both halves and declines to reconcile them. The one thing that is **not** acceptable is today's behavior, where the answer depends on the order two events arrived in.

### C.3 M4 — Table 8.9 Note (1), and the Completed-parent variant

**Spec, verbatim.** Table 8.9, Notes, printed p.119:

> "(1) If the exception is fixed and the restart transition is taken to Active, then it should continue transition into Suspended state.
> (2) Return the child to the state it has before the "parent suspend" or "suspend" transition to Suspended state."

Note (1) is attached to the `Failed(1)` cells, which appear in exactly two rows: `resume, parent resume → Active` with child `Failed → Failed(1)`, and `suspend, parent suspend → Suspended` with child `Failed → Failed(1)`. So Note (1) governs a Failed child that survived its parent's suspension and is subsequently re-activated.

**Design — the Suspended-parent case.**

```
on Transition(instance, reactivate):        // Failed -> Active, Table 8.8 "re-activated"
    Apply:  fire reactivate                             // journals Transitioned(Failed -> Active)
            agenda.Push(PropagateContainment(instance))  // operation 6

PropagateContainment(instance):
    container = nearest ancestor whose state constrains containment
    if container.State == Suspended:
        fire parentSuspend on instance                   // Active -> Suspended
        instance.ParentSuspendState = Active             // Note (2): resume returns it to Active
```

Two journaled transitions, not one — Note (1) says "continue transition into Suspended state", which is a second transition, and the audit trail should show both. `ParentSuspendState = Active` is what makes the later `parent resume` land back in Active, per Note (2) and Table 8.8's `parent resume` row ("Transition to the state previous to be suspended").

`PropagateContainment` is a general operation, not a `reactivate` special case: it runs after any transition that could place an instance in a state its container cannot contain. That makes the rule stateable as one invariant:

> **I-3 (containment).** A plan-item instance's state must be a state its nearest constraining ancestor's Table 8.9 row permits.

**The Completed-parent variant — no Table 8.9 row exists.**

The configuration is reachable: Table 8.9's `complete` row permits a child in `Failed → Failed` under a Completed parent (printed p.119). Re-activating that child yields **Active under Completed**, which the same row marks `<impossible>`. Table 8.9 cannot help, because it describes *parent-initiated* propagation, not child-initiated re-entry. (A Terminated parent is not reachable: the `exit, terminate` rows drive `Failed → Terminated`, so no Failed child survives a terminated parent.)

Three options:

| | Behavior | For | Against |
|---|---|---|---|
| **(i)** | **Refuse** the `reactivate`. `CanFire` returns false; the public surface throws. | Preserves I-3. Observable rather than silent. Table 8.8's own rationale for `re-activated` — "when the source of the failure has been resolved" — presupposes a live container in which to resume work; there is none. | CMMN states no such refusal. Inventing a guard is a deviation. |
| (ii) | Allow `reactivate`, then immediately `exit` → Terminated, mirroring #179's complete-cascade. | Uses an existing, spec-grounded cascade. | Produces a transition sequence Failed→Active→Terminated that no table describes, and destroys the Failed record the audit trail wanted. |
| (iii) | Allow it and accept `Active` under `Completed`. | No invention. | Produces exactly the configuration Table 8.9 calls impossible, silently. |

**Decision D-C3-1: (i), refuse.** It is the only option that fails loudly.

> **Open question OQ-4.** (i) is a deviation — an engine-invented guard. Reviewers should decide whether the guard belongs at the transition (a `CanFire` predicate, so it also blocks any future engine-internal path) or only at the public `Trigger` surface (so the engine could still do it internally if a future need arises). This design assumes the transition, which is stricter.

### C.4 The cascade tables (context for C.2/C.3)

Table 8.9's type asymmetries are already implemented correctly and are preserved verbatim (§G.1). Restating the two that a rewrite most easily breaks, from the PDF (printed pp.117-119):

- Under a **Completed** Stage: Stage/Task children in {Available, Enabled, Active, Suspended} are `<impossible>`; Disabled → Disabled; Failed → Failed. **Milestone/EventListener** children in Available stay **Available**, and in Suspended stay **Suspended** — they legitimately survive a completed parent.
- Under a **Failed** Stage (`fault` rows): **nothing propagates.** Every child stays where it is. Table 8.8's `fault` row is explicit: "This state MUST NOT propagate."

In the redesign these become one table-driven function over `(parentTransition, childKind, childState) → childTransition?`, replacing the switch ladders in `StageBehavior.HandleParentTransitioned` / `TaskBehavior` / `MilestoneBehavior` / `EventListenerBehavior`. The four handlers currently duplicate and diverge from each other; one table cannot.

### C.5 M5 — the case-worker allowlist on the public `Trigger` surface

**Spec basis.** Each transition's actor is stated in the Description column of Tables 8.6 (printed p.113), 8.8 (printed pp.115-117), and 8.11 (printed pp.120-121). Reading them off:

| Element | Case-worker / administrator transitions | Engine-only |
|---|---|---|
| casePlanModel (Table 8.6) | `suspend`, `terminate`, `complete`, `re-activate` ("by a Case worker (human), or an administrator"), `close` ("by the system, an administrator, or Case worker (human)") | `create`, `fault` |
| Stage / Task (Table 8.8) | `disable`, `manual start`, `suspend`, `resume`, `re-enable`, `re-activated`, `terminate`, `complete` | `create`, `enable`, `start`, `exit`, `fault`, `parent suspend`, `parent resume` |
| EventListener / Milestone (Table 8.11) | `suspend`, `resume`, `terminate`; **`occur` only for a `UserEventListener`** | `create`, `occur` (Milestone: "when one of the achieving `Sentries` is satisfied"; TimerEventListener: a tick), `parent terminate` |

Table 8.11's `occur` row is explicit about the split: "For event listener transitions when the event being listened by the `EventListener` instance does occur. For a `UserEventListener` instance this transition happens when a `Case` worker (human) decides to raise the event. For `Milestone` instance transitions when one of the achieving `Sentries` (entry critera) is satisfied."

**Design.**

1. A single static allowlist table keyed by `(elementKind, transition)`, consulted in the command preamble (step 2 of §B.1), **before** anything touches the agenda.
2. Rejection is an explicit, distinguishable error — **not** the current silent no-op via `HandleUnhandledTrigger`. Three outcomes must be distinguishable at the API:
   - transition not permitted for a case worker on this element kind → `ResultStatus.BadRequest`
   - transition permitted but not fireable from the current state → `ResultStatus.Conflict`
   - caller lacks the required role (`UserEventListener.authorizedRoleRefs`) → `ResultStatus.Unauthorized`
3. **Narrow the wire enum.** `Wayfinder.Contracts.V1.PlanItemTransition` currently exposes all 18 values including `ParentSuspend`, `ParentResume`, `Create`, `Start`, `Enable`, `Exit`, `Fault`, and the non-spec `ParentTerminate`. Replace it with a `CaseWorkerTransition` enum containing only the allowlisted set, so the illegal values are *unrepresentable* rather than rejected at runtime.
   - This also removes `ParentTerminate` from the wire, which is finding **I3** (`Spec.CMMN.MODEL.cs:2760-2763` hand-appends it to the XSD-generated enum; Table 5.31, printed pp.35-36, enumerates exactly 17 values without it). The *interchange* half of I3 remains orthogonal and out of scope.
   - It is a breaking wire change. There are no released versions and no external consumers (§E.1), so it is free now and expensive later.

**Concrete defects this closes.** `Trigger(ParentSuspend)` on an Available task currently suspends it *without* raising `ParentSuspended` (that event is only raised in `HandleParentTransitioned`), so `ParentSuspendState` stays null and the only exits after a recycle are `Resume`→Active — a task that was never started becomes Active, skipping its entry criteria and the ManualActivationRule — or `Exit`. `Trigger(Fault)` fabricates a "software failure". Both become unrepresentable.

### C.6 M6 a–c — suspension preserves pending satisfactions and ticks

**Spec basis.** Table 8.9 Note (2) (printed p.119): "Return the child to the state it has before the 'parent suspend' or 'suspend' transition to Suspended state." Table 8.8 `parent resume` (printed p.117): "Transition to the state previous to be suspended, when the parent stage transitions out of Suspended." Table 8.10 (printed p.120), Suspended: "…temporarily suspend an `EventListener` instance for which the event has not yet occurred, or to suspend a `Milestone` instance that has not been reached." Suspension is a pause with memory in every place the spec describes it.

**One mechanism for all three cases.**

```csharp
internal sealed record DeferredEffect(
    long        EventOrdinal,      // when it was earned — replay order is this order
    EffectKind  Kind,              // CriterionSatisfaction | RepetitionRequest | TimerTick | BottomUpActivation
    string      SentryInstanceId,  // for CriterionSatisfaction
    long        SatisfactionOrdinal,
    string      TimerInstanceId);  // for TimerTick
```

> **R-C6.** An agenda operation targeting an instance that is `Suspended`, or that has any `Suspended` ancestor, does not drop its effect and does not apply it. It appends a `DeferredEffect` to that instance's `PendingWhileSuspended` list and journals `EffectDeferred`. The `resume` / `parent resume` transition drains that list, in recorded order, as enqueued agenda operations, before the transition's own successors.

Draining as *enqueued operations* rather than inline calls matters: a deferred satisfaction that spawns a repetition must go through the same `Instantiate` operation as any other, so the completion ordering of §B.2 still holds.

**(a) Entry-criterion satisfaction delivered to a Suspended item.**

Today `HandleSentrySatisfied`'s branch ladder (`TaskBehavior.cs:239-275`, mirrored at `StageBehavior.cs:353-389`) routes *any* non-Available state into the `OnPartOccurred && EvaluateRepetitionRule()` arm, which unsubscribes the original's entry criteria and marks it `Repeated` — permanently stranding the original while a phantom sibling spawns.

New rule, split cleanly by **what the item's pre-suspension state was**:

```
ConsumeSatisfaction(criterion, satisfaction):
    owner = criterion.Owner
    effectiveState = owner.State == Suspended ? owner.ParentSuspendState : owner.State

    if owner or any ancestor is Suspended:
        defer(owner, CriterionSatisfaction)                      // R-C6
        return

    switch effectiveState:
      Uninitialized  -> fire create, then re-run this operation  // cross-stage / bottom-up, §C.11
      Available      -> ManualActivationRule ? enable : start    // Table 8.8, printed p.115
      Enabled|Active -> if satisfaction.OnPartOccurred && RepetitionRule: Instantiate(parent, item, rep+1)
      Disabled       -> journal SatisfactionIgnoredWhileDisabled; DO NOT spawn a repetition
      terminal       -> journal SatisfactionIgnoredAfterTerminal
```

**`Disabled` is explicitly not a repetition-trigger state.** §8.6.4 (printed p.122) grants the repetition trigger to "an entry criterion with an `OnPart` is satisfied" for an instance that has already left Available; a Disabled instance is one a Case worker deliberately excluded from this case, and spawning a sibling around that decision has no Table 8.8 basis. Today's ladder makes it eligible; this design does not.

The satisfaction itself is still **recorded** on the sentry regardless (§8.5 defines OnPart satisfaction as a past event; nothing un-satisfies it). Only the *consumption* defers.

**(b) A Suspended Milestone missing its achieving sentry.** Falls straight out of R-C6. `MilestoneBehavior.cs:96-118` currently journals `EntryCriterionSatisfied` and fires nothing, with no resume hook. Under R-C6 the satisfaction is deferred; Table 8.11's `resume` lands the milestone back in Available; the deferred consumption then fires `occur` (Available → Completed) as a second transition in the same turn. Legal, and ordered.

**(c) A timer tick during suspension.** A reminder that fires while the TimerEventListener instance (or any ancestor) is Suspended records `TimerFired` — the time genuinely elapsed — and defers the `occur`. The remaining-occurrence count (§C.7b) is decremented **once**, at fire time, not at drain time.

The hard sub-case: an `R5/PT1M` timer suspended for ten minutes. CMMN says nothing.

| | Behavior on resume | Trade-off |
|---|---|---|
| **(α)** | **One** catch-up occurrence; remaining count decremented by one; schedule re-anchored to the resume instant. | Simple; produces one distinguishable event. Loses the fact that ten periods elapsed. |
| (β) | N occurrences replayed, one per elapsed period. | Faithful to elapsed time. But N synthetic occurrences land in one turn with identical effective timestamps; a sentry watching the listener would satisfy N times with nothing to distinguish the occurrences, and §8.6.4's repetition machinery would spawn N instances instantaneously. |
| (γ) | Missed ticks discarded; schedule resumes from the next future period. | Matches `PeriodicTimer`'s own coalescing (§D.1.3). Loses the occurrence entirely, which is termination-in-effect — the defect M6c exists to fix. |

**Decision D-C6-1: (α).**

> **Open question OQ-5.** (α) is a judgement call, not a spec reading. If a model uses a timer as an SLA counter, (β) is what the author meant. Worth deciding whether this should be a per-model attribute rather than an engine-wide policy.

### C.7 M7 a–c — timer semantics

`Iso8601.cs` carries over. It is wrong today and stays wrong unless the following is implemented. All three sub-findings were re-verified against the PDF and, for (c), **empirically against NodaTime 3.3.2** (the pinned version) — output reproduced below.

**Spec basis.** Table 5.19 (printed p.26):

> `timerExpression : Expression [0..1]` — "An optional expression that MUST evaluate to an ISO-8601 conforming representation for date and time, duration, or interval."
> `timerStart : StartTrigger[0..1]` — "…If `timerStart` is specified, then at runtime, if the trigger occurs the time of occurrence of the trigger is captured and the `timerExpression` SHOULD be relative to the timestamp captured when the `timerStart` trigger occurs."

Table 5.55 (printed p.56) gives EventListener `N/A` for RepetitionRule. §5.4.11.3 (printed p.54): "`EventListeners` cannot have `RepetitionRule`. … However, for a `TimerEventListener` repetition can be defined via a `timerExpression` based on ISO-8601, by defining repeating intervals in it (using `"R<n>/"` notation)."

#### C.7a Duration-only first fire, and the full form table

Today `ConfigureTrigger` (`TimerEventSchedulerGrain.cs:71-94`) does `StartAt(timerStart ?? schedule.Start ?? DateTime.UtcNow)` and puts the duration only into `WithInterval(...)`. A non-repeating Quartz simple trigger fires once **at `StartAt`** and never consults the interval, so `PT15M` occurs at creation.

**Design.** Define the **anchor** first:

> anchor = the `timerStart` trigger's occurrence instant, if `timerStart` is specified;
> otherwise, the instant the TimerEventListener instance entered `Available`.

Then, exhaustively, for every ISO form `Iso8601` can parse:

| Expression form | First fire | Subsequent fires (only if `R` present) |
|---|---|---|
| `<instant>` | at `<instant>` | n/a — an instant has no interval |
| `<duration>` | `anchor + duration` | `previous + duration` |
| `<start>/<end>` | at `<end>` | `previous + (end − start)` |
| `<start>/<duration>` | `start + duration` | `previous + duration` |
| `<duration>/<end>` | at `<end>` | `previous + duration` |

The unifying rule: **a timer fires at the *end* of the interval it describes.** A bare instant is a zero-length interval at that instant. This is a deviation only in that the spec never says it; it is the reading that makes `PT15M` mean "in fifteen minutes", which is unambiguously the intent of Table 5.19's `timerStart` sentence.

**Conflicting starts.** `timerStart` present *and* an expression carrying an explicit `<start>` is currently a fault (`TimerEventListenerBehavior.cs:89-106`) that can never fire, because `ConfigureForMilestoneOrEventListener` permits no `fault`. Design: make it a **deploy-time lint error** — both are static model properties, so this is decidable before the case ever runs. See §C.7c for the general runtime story.

**New finding — `R<n>` is off by one.** `Iso8601.Repetitions` (the integer after `R`) is passed straight into Quartz's `WithRepeatCount(n)` (`TimerEventSchedulerGrain.cs:87`), which means "repeat **n more times after the first fire**" — n+1 occurrences total. ISO 8601's `Rn/<interval>` denotes n repetitions of the interval, and every mainstream engine reads `R3/PT10S` as three fires. **Design: `R<n>` = exactly n occurrences. `R/` with no number = unbounded. `R0/` = never fires** (and a lint warning, since it is almost certainly an authoring mistake). This must be pinned by a test before the Quartz code is deleted, so the change is visible rather than silent.

#### C.7b The R-bound must decrement

Today every post-Completed tick publishes a repetition request (`TimerEventListenerBehavior.cs:210-218`), and each spawned instance **re-evaluates the timerExpression and re-schedules the full `R<n>` schedule afresh** (`:81-138`). `R2/PT1H` therefore chains until the #67 ceiling faults the containing case, and the Completed predecessor's Quartz job is never cancelled (only `Terminated` cancels, `:152-154`), leaking jobs.

**Design.** The schedule is **evaluated once, when the timer is armed**, and persisted as instance state:

```csharp
internal sealed class ArmedTimer
{
    string   PlanItemInstanceId;
    Instant  Anchor;
    IsoSchedule Schedule;            // parsed once; never re-derived from the expression
    int?     RemainingOccurrences;   // null = unbounded (R/ with no count)
    Instant  NextFireUtc;
    string   ReminderName;           // §D.1.4
}
```

Each fire: journal `TimerFired { timerInstanceId, occurrenceIndex, remaining }`, decrement `RemainingOccurrences`, recompute `NextFireUtc`. At zero, journal `TimerDisarmed` and unregister the reminder.

**How repeated occurrences reach sentries.** Table 8.11 makes `occur` a one-way Available→Completed transition, so a single TimerEventListener instance cannot occur twice. Today's model — each post-Completed tick spawns a *fresh* TimerEventListener instance which then occurs — is retained, because it respects the instance lifecycle and §5.4.11.3 explicitly sanctions timer repetition. What changes is the **bound**: the spawn is driven by and limited to `RemainingOccurrences`, **not** by a RepetitionRule the element cannot have (Table 5.55). The successor inherits `ArmedTimer` state rather than re-parsing the expression. The #67 ceiling remains as an independent backstop.

[03](03-cmmn-execution-semantics.md)'s conformance table frames #182 as "timer repetition ignoring the rule". That framing is wrong and should be corrected with this work: there is no rule to ignore.

#### C.7c ISO-8601 parsing rejects conforming values

`Iso8601.cs:78-119` pipes durations through `PeriodPattern.NormalizingIso.Parse(...).Normalize().ToDuration()`, and instants through `InstantPattern.ExtendedIso`. **Verified empirically** against NodaTime 3.3.2 (a throwaway probe, exact output):

```
DURATION OK   PT15S -> 00:00:15
DURATION OK   PT15M -> 00:15:00
DURATION OK   P1D   -> 1.00:00:00
DURATION OK   P1W   -> 7.00:00:00
DURATION FAIL P1M        -> InvalidOperationException: Cannot construct duration of period with non-zero months or years.
DURATION FAIL P1Y        -> InvalidOperationException: Cannot construct duration of period with non-zero months or years.
DURATION FAIL P1Y2M3DT4H -> InvalidOperationException: Cannot construct duration of period with non-zero months or years.
INSTANT OK   2026-08-03T12:00:00Z     -> 2026-08-03T12:00:00.0000000Z
INSTANT OK   2026-08-03T12:00:00.500Z -> 2026-08-03T12:00:00.5000000Z
INSTANT FAIL 2026-08-03T12:00:00+02:00 -> UnparsableValueException: The value string does not match a quoted string in the pattern.
INSTANT FAIL 2026-08-03T12:00:00       -> UnparsableValueException: The value string does not match a quoted string in the pattern.
```

All four failing values are conforming per Table 5.19. Today the exception is caught, logged, and folded into a `Failure` — the timer silently never fires. None of these cases appear in `Iso8601Tests`.

**Design.**

1. **Do not collapse a `Period` to a `TimeSpan`.** Keep the parsed `Period` (years/months/weeks/days + time components) and compute the next fire as calendar arithmetic on the anchor: `anchor.InZone(zone).LocalDateTime.Plus(period).InZoneLeniently(zone).ToInstant()`. This is the only correct handling — `P1M` from 31 January is not a fixed number of seconds, and `ToDuration()` is *right* to refuse.
2. **Accept offsets and offset-less local date-times.** Replace `InstantPattern.ExtendedIso` with `OffsetDateTimePattern.ExtendedIso`, falling back to `LocalDateTimePattern.ExtendedIso`.
3. **A parse failure must be loud.** Split into two paths:
   - **Deploy time.** A `timerExpression` whose body is a string literal (which every corpus sample is) is statically parseable. Add a `CmmnCapabilityLint` rule that parses it and emits a **blocking** finding on failure — the same tier `ConformanceHarness` already throws on. This catches the overwhelming majority of real cases before a case ever runs.
   - **Runtime.** For a genuinely dynamic expression, journal `TimerExpressionInvalid { error }`, log at Error, and surface it on the case snapshot. It **cannot** fire `fault`: `ConfigureForMilestoneOrEventListener` permits no Failed state for EventListeners, and Table 8.11 (printed pp.120-121) defines none. Making it fire would be a state-machine change the spec does not support, and this design does not make it.

> **Open question OQ-6 — time zone.** Calendar arithmetic needs a zone, and CMMN never mentions one. An offset-less `2026-08-03T12:00:00` is ambiguous. Options: (i) reject offset-less values (status quo — rejects conforming input, so no); (ii) assume UTC everywhere, with a lint warning; (iii) a per-tenant configured zone, defaulting to UTC. This design assumes **(ii)** because it is the smallest change that stops rejecting conforming input, and notes that (iii) is the only option that gets `P1M` and DST right for a real business calendar. Reviewers should decide whether (iii) belongs in this redesign or in a follow-up.

### C.8 The sentry satisfaction algebra (C4 + M8 + M9 + M10)

This is the core deliverable. C4 and M9 currently **cancel each other**: an IfPart-bearing CaseFileItemOnPart sentry only works because C4's latched fall-through publishes past M9's null-token latch. Fixing C4 alone surfaces M9 as a regression. They are therefore specified together, as one replacement algebra.

**Spec, verbatim.** §8.5 (printed p.121):

> "A `Sentry`'s `OnPart` is satisfied when one of the following conditions is satisfied:
> • For a `PlanItemOnPart`, its `Sentry` referred by `sentryRef` has occurred.
> • For a `PlanItemOnPart` or `CaseFileItemOnPart`, its `sourceRef` transitions into the transition described by the `standardEvent` (`PlanItemTransition`, or `CaseFileItemTransition`).
>
> A `Sentry` is satisfied when one of the following conditions is satisfied:
> • All of the `OnParts` are satisfied AND the `IfPart` condition evaluates to TRUE.
> • All of the `OnParts` are satisfied AND there is no `IfPart`.
> • The `IfPart` condition evaluates to TRUE AND there are no `OnParts`.
>
> Entry criterion sentries are considered ready for evaluation while the task, stage, or milestone is in Available state. Exit criterion sentries are considered ready for evaluation while the `CasePlanModel`, `State`, or `Task` is in Active state. Sentries are evaluated when events arrive to the system or when events are generated by the system. A single event may satisfy multiple sentries. Sentries with no `OnPart` must have an `IfPart`, and that `IfPart` will be evaluated for all `CaseFileItem` events because `IfPart` expressions are based on `CaseFileItem` properties."

Table 5.26 (printed p.33): "A `Sentry` MUST have an `IfPart` or at least one `OnPart`."

#### Decision D-C8-1 — one sentry instance per *criterion instance*

Today one `SentryGrain` exists per `(owning stage instance, sentry definition)`, shared by every criterion referencing it, and minted with a fresh `ShortGuid` on **every stage activation** — which is finding C5 (each idle reactivation adds a duplicate live sentry set).

**Design: a sentry instance is owned by the criterion instance that references it.** Each plan-item instance gets its own private copy of every sentry its entry/exit criteria name. Consequences:

- **C5 dies twice over**: sentry instances are created exactly once, inside the `Instantiate` operation, recorded in the journal, and reconstructed by the fold. There is no per-activation minting.
- **#177 gets much simpler.** A repetition instance needs fresh entry-criterion state; with per-criterion sentries it gets one automatically, with empty occurrence memory. Today's shared, latched sentry is the whole reason a spawned instance "can miss the very satisfaction that created it".
- **Evaluability (§8.5's "ready for evaluation while…") becomes local.** A sentry copy knows exactly one owner and one criterion kind, so the gate is a single field read. With a shared sentry the gate is per-referrer and the sentry could be "satisfied for one referrer and not another" — a state with no clean representation.
- **Cost:** an IfPart shared by *k* criteria is evaluated *k* times per relevant event. Bounded, in-memory, and mitigable by memoizing `(expression body, contextRef, case-file version)` within a turn — sound under invariant I-2 (§B.4.1).

> **Open question OQ-7.** The alternative (one sentry instance per owning-stage-instance, shared) is closer to today's topology and cheaper. It requires satisfaction to fan out to referrers and consumption to be tracked per referrer, which reintroduces the "satisfied for whom?" problem. This design takes per-criterion. Reviewers should weigh the duplicate-evaluation cost against the #177 simplification.

#### The algebra

For a sentry instance `S` with OnParts `O₁..Oₙ` (n ≥ 0) and optional IfPart `φ`:

```
State:  Pending[i]      : Occurrence?         // most recent UNCONSUMED occurrence of Oᵢ
        Satisfactions   : list<SatisfactionRecord>    // a LEDGER, not a bool
```

**An occurrence is identified by the case-monotonic event ordinal of the transition that produced it.** That ordinal is the journal position, assigned by the single writer. This is the direct replacement for `SentryStore`'s `OccurrenceToken` and is where **M8** dies: two distinct occurrences by the same source instance necessarily carry different ordinals, and a "redelivery" cannot exist at all because there is no transport. `SentryStore.IsRedelivery` is deleted.

**Evaluation** — operation 2, `RecordAndEvaluateSentry`:

```
1. Pending[i] := occurrence                      // record; last-wins per OnPart
2. if n > 0 and any Pending[j] is null:  stop    // AND-join incomplete
3. if φ exists:
     evaluate φ against the CURRENT case file
     faulted -> journal SentryIfPartFaulted; CONSUME NOTHING; stop
     false   -> journal SentryNotSatisfied{IfPartFalse}; CONSUME NOTHING; stop
     true    -> satisfied
   else: satisfied
4. satisfied:
     journal SentrySatisfied { sentryInstanceId, consumedOrdinals[], satisfactionOrdinal, onPartOccurred }
     Pending[*] := null                          // consume
     agenda.Push(ConsumeSatisfaction(owningCriterion, thisSatisfaction))
```

**Steps 1 and 2–4 are one atomic operation and must never be split.** If recording and evaluating were separate agenda entries, two occurrences of the same OnPart arriving in one turn would both be recorded before either is evaluated; the second overwrite would erase the first and one satisfaction would be silently lost. Recording-and-evaluating together makes that impossible.

**What each finding maps to:**

- **C4** — "a satisfied sentry re-fires even when its IfPart evaluates FALSE." Today `SentryGrain.cs:367` gates the publish on `if (!State.Satisfied) return;` where `SentryStore.Satisfied` is a monotonic latch never cleared (`SentryStore.cs:75-79`). The IfPart-FALSE branch raises `OnPartNotRearmed` and then **falls through to the latched publish**. In the new algebra there is no latch to consult: step 4 is the only publish point and is reachable only from step 3's `true` branch. `SentryStore.Satisfied` as a `bool` is **deleted**; satisfaction becomes a ledger of discrete events.
- **M9** — "CaseFileItemOnPart without IfPart latches after the first occurrence." Today `SentryGrain.cs:203` passes `occurrenceToken: null`, and `IsRedelivery` treats any null-token occurrence as a redelivery once one is recorded. Gone: every occurrence carries an event ordinal, so a second CFI update produces a fresh `Pending[i]`, step 2 passes, no IfPart, satisfied again. Table 5.29 (printed p.34) makes six of the eight `CaseFileItemTransition` values repeatable Available→Available transitions (`update`, `replace`, `addChild`, `removeChild`, `addReference`, `removeReference`), so this is the common case, not an edge.
- **The C4/M9 cancellation** — both defects vanish in the same change, which is the only safe way to remove either.
- **M10** — "IfPart never re-evaluated on CaseFileItem changes; `OnPartNotRearmed` can make a sentry permanently unsatisfiable." Two halves, two fixes:
  1. **Do not consume on FALSE.** Today `Apply(OnPartNotRearmed)` *deletes* the triggering occurrence (`SentryStore.cs:99-103`). §8.5 defines OnPart satisfaction as a past event — "its `sourceRef` transitions into the transition described by the `standardEvent`" — and nothing in §8.5 un-satisfies it. The occurrence therefore **stays Pending**, and a later re-evaluation can satisfy the sentry. `OnPartNotRearmed` is deleted.
  2. **Re-evaluate on case-file events.** Every case-file item transition enqueues `RecordAndEvaluateSentry` (with no new occurrence) for **every sentry instance that has an IfPart and whose owning criterion is currently evaluable** — entry criteria while the owner is Available, exit criteria while the owner is Active, per §8.5's "ready for evaluation while…" sentence. Step 2 short-circuits cheaply when the AND-join is incomplete, so the cost is O(sentries) field reads plus one expression evaluation per sentry whose join is already complete.
  This makes the review's canonical M10 scenario work: B completes while `amount == 50` → occurrence stays Pending, IfPart false; later `amount = 200` → the case-file event re-evaluates → satisfied.
- **`Pending` is last-wins, not a queue.** §8.5's AND-join has no notion of pairing occurrences across OnParts, and a queue would invent an arbitrary pairing rule. Last-wins loses nothing observable: with the atomic record-and-evaluate above, an earlier occurrence is only overwritten while a *sibling* OnPart's join is still incomplete, where the older occurrence is exactly the one that should be forgotten.
- **`sentryRef` OnParts.** §8.5's first bullet ("its `Sentry` referred by `sentryRef` has occurred") is satisfied when the referenced `ExitCriterion`'s own sentry instance appends a satisfaction. Table 5.30 (printed p.35) constrains this: "When `sentryRef` is specified, `standardEvent` MUST have value 'exit.'" and the referenced ExitCriterion must be contained by the PlanItem named by `sourceRef`. Both are lintable; the runtime match is a direct reference, not a string search. This is preserved end-to-end (§G.1) but the Stateless parameterized-`Exit` trick that currently threads `exitCriterionRef` through `Transition.Parameters` (`PlanItemStateMachine.cs:25,98-107`; `BaseBehavior.cs:166-168`) becomes unnecessary — the agenda operation carries it directly.
- **Standalone-IfPart sentries** (n = 0). §8.5's third bullet. Evaluated on every case-file item transition. Today `SentryGrain.cs:216` returns early on `TentativeState.Satisfied`, so such a sentry satisfies at most once per case — defensible for an entry criterion, **wrong as the exit criterion of a repeating item** (instance 2+ can never exit). With per-criterion sentry instances (D-C8-1), each repetition gets its own copy and the problem disappears. The case-wide sentinel stream (`CaseFileItemAddress.CaseWideSentinel`) and its dedicated publish (`CaseFileItemGrain.cs:334-345`) are deleted.

#### I7 — the OnPart match predicate

Current (`SentryGrain.cs:174-178`, and the CFI twin at `:192-195`):

```csharp
.SingleOrDefault(x =>
    x.StandardEvent == @event.StandardEvent &&
    (string.IsNullOrWhiteSpace(x.SourceRef) || x.SourceRef.Equals(@event.SourceDefinitionId) &&
    (string.IsNullOrWhiteSpace(x.ExitCriterionRef) || x.ExitCriterionRef.Equals(@event.ExitCriterionRef))));
```

`&&` binds tighter than `||`, so this parses as:

```
StandardEvent match && ( blank(SourceRef) || ( SourceRef match && (blank(ExitCriterionRef) || ExitCriterionRef match) ) )
```

A blank `SourceRef` therefore matches **any** source with the right `standardEvent` **and** bypasses the `exitCriterionRef` filter entirely. Latent only while models populate `sourceRef` — one malformed import from live.

**Design.**

```
match(onPart, event) :=
       onPart.StandardEvent == event.StandardEvent
    && (!blank(onPart.SourceRef)        && onPart.SourceRef == event.SourceDefinitionId)
    && (blank(onPart.ExitCriterionRef)  || onPart.ExitCriterionRef == event.ExitCriterionRef)
```

Note the middle conjunct is **not** the parenthesised original: a blank `SourceRef` now **fails to match**, rather than matching everything. Table 5.30 (printed p.35) types `sourceRef : PlanItem[0..1]` — optional in the XSD — but its own description presupposes an associated PlanItem ("If the associated `PlanItem` is undergoing a state transition as specified by attribute `standardEvent`, the `OnPart` MUST occur"). With no associated PlanItem there is nothing that MUST occur. So: **fail closed at runtime, and add a `CmmnCapabilityLint` rule** flagging a `PlanItemOnPart` with no `sourceRef`. (`CaseFileItemOnPart.sourceRef` is `[1]`, mandatory — a missing one is a schema violation.)

**`SingleOrDefault` → `Where`.** Today two legitimately matching OnParts throw *inside a stream handler*, producing a poison-retry-then-drop. In the new model there is no stream to poison, but the throw would abort the turn. Design: **every matching OnPart records an occurrence**; the sentry iteration is `Where`, not `SingleOrDefault`, at both the OnPart level and the sentry level. No model shape may throw inside the loop.

### C.9 Sentry evaluation timing (the "ready for evaluation" gate)

`SentryGrain` today publishes `SentrySatisfiedEvent` purely from OnPart/IfPart evaluation with **no check at all** on the referencing PlanItem's own state (this is why `StageBehavior.HandleSentrySatisfied` needs the defensive ladder it has). §8.5 is explicit that evaluation is state-gated. In the new model the gate is enforced at the sentry, because a sentry instance has exactly one owner:

- **Entry criterion**: evaluable while the owner is `Available` — or `Uninitialized`, for the cross-stage bottom-up case (§C.11), where the owner does not exist yet.
- **Exit criterion**: evaluable while the owner is `Active` (§8.5: "while the `CasePlanModel`, `State`, or `Task` is in Active state" — "State" is a typo for "Stage" in the published spec).
- **Suspended owner**: the occurrence is recorded, and consumption defers (§C.6).

Occurrences are **always recorded**, regardless of evaluability — that is #186's discipline ("journal the satisfaction unconditionally… the journaled FACT that the criterion was satisfied is not conditional") generalised.

### C.10 (Reserved — see §C.4 for the cascade tables.)

### C.11 M11 / #176 — cross-stage OnParts, bottom-up activation, and the duplicate-instance guard

**Spec, verbatim.** Table 8.7, `Available`, printed p.114:

> "A `Stage` or `Task` instance becomes available when
> • The `Stage` instance in which it resides moves into Active state.
> or
> • The `Stage` or `Task` has a `Sentry` with an `OnPart` that has a `sourceRef` outside of the enclosing `Stage` of that `Stage` or `Task` and this `Sentry` is satisfied. In that case, the `Stage` instance and recursively all `Stage` instances up to the enclosing `Stage` of the `Stage` or `Task` in which the `Stage` or `Task` resides moves into Active state if not already active.
>
> While available, the `Stage` or `Task` instance is waiting for its entry criteria (`Sentry`) to become TRUE. A missing entry criteria (`Sentry`) is considered TRUE."

**Design, in four parts.**

**1. Delete the scope filter.** `SentryGrain.cs:172` (`if (!@event.SourceScope.StartsWith(_scope)) return;`) exists only because streams are scoped. With one grain, every transition is visible to every sentry instance. Matching is purely `sourceRef`/`standardEvent`/`exitCriterionRef` (§C.8's `match`). Table 5.25's constraint — that a *criterion* must reference a Sentry contained by its own Stage or PlanFragment — is unchanged and stays a lint rule; it constrains the criterion→sentry edge, not the OnPart→source edge.

**2. Source instance selection.** A `sourceRef` names a `PlanItem` *definition*, which may have several live instances. Per §8.6.4 Example 1 (printed p.123) — B, B′ and B″ each independently triggering A's OnPart — the OnPart occurs on **any** instance of that definition. Match by definition id, any instance; the occurrence carries the specific source instance id for the audit trail.

**3. Bottom-up activation, with the ordering that makes the duplicate guard unnecessary.**

The hazard [04 §M11](04-adversarial-review-2026-08.md) warns about: "the bottom-up block fires Start on the parent, whose `HandleEnterActiveFromStart` unconditionally creates one instance of *every* child PlanItem with no check against existing `StageStore.Children` — including a duplicate of the very child whose Available event triggered activation."

The fix is ordering, not a guard bolted on afterwards:

```
BottomUpActivate(satisfaction, targetPlanItemModel):
    chain = [enclosing Stages of target, from outermost to innermost,
             stopping at the enclosing Stage of the source item]

    for stage in chain (OUTERMOST FIRST):
        if stage has no live instance:      Instantiate(parent(stage), stage, rep 0)
        switch stage.State:
          Available            -> fire start          // Table 8.7: "moves into Active state if not
          Enabled              -> fire manualStart    //   already active" — overrides ManualActivationRule
          Disabled             -> REFUSE (see below)
          Suspended            -> defer (R-C6); stop
          Active               -> continue
          terminal             -> REFUSE

    // the innermost stage's activation has already instantiated `target` as part of its
    // normal child fan-out. Find it; do not create it.
    targetInstance = innermost.Children.single(c => c.Item.Id == targetPlanItemModel.Id && c.Repetition == 0)
    agenda.Push(ConsumeSatisfaction(targetInstance.EntryCriterion(satisfaction.Sentry), satisfaction))
```

Because activation runs **outermost-first**, the innermost stage's `HandleEnterActiveFromStart` creates the target as an ordinary child. The bottom-up path then *finds* it. There is no second creation path and therefore no duplicate to guard against.

As defence in depth, child instantiation is made idempotent regardless:

> **R-C11.** `InstantiateChildren(stage)` creates a repetition-0 instance only for `PlanItem`s that have **no** existing instance under that stage instance. Repetitions are created exclusively by the explicit `Instantiate(stage, item, rep>0)` operation.

And the satisfaction itself cannot be lost between "target has no instance" and "target has an instance" — this is #177 in a new guise — because the occurrence is recorded on the sentry *before* the activation and consumed *after*, both inside one turn and one journal write.

**4. `Disabled` ancestors: refuse, do not auto re-enable.** Today's ancestor ladder (`StageBehavior.cs:578-585`) fires `Reenable` from Disabled and `ManualStart` from Enabled. `ManualStart` is defensible — Table 8.7 says the chain "moves into Active state", which necessarily overrides the ManualActivationRule. `Reenable` is not: Table 8.8's `re-enable` row (printed p.116) reads "Transition by a `Case` worker (human) decision", and auto-firing it silently overrides an explicit human decision to exclude that stage from this case.

**Design: refuse.** Journal `BottomUpActivationRefused { blockingInstanceId, blockingState }`, log at Warning, and leave the satisfaction recorded (so it can still be consumed if a Case worker later re-enables the stage). This is a **behavior change** from today and should be flagged in review as such.

**5. Lint.** With the fix, cross-stage `sourceRef`s work, so no "unsupported" lint rule is needed. What *is* needed is a rule for a `sourceRef` that resolves to no `PlanItem` at all — currently such a model deploys clean and silently never fires.

---

## D. Questions answered

### D.1 Orleans reminder granularity — **verified, not assumed**

Everything below was read from the decompiled shipped assemblies in the local NuGet cache and cross-checked by reflection at runtime against **Orleans 10.2.1**, the version pinned uniformly across every `.csproj` in this repo. Where a fact could not be established it is marked.

#### D.1.1 The type is not where you expect it

**`ReminderOptions` lives in `Orleans.Hosting`, not `Orleans.Configuration`,** in Orleans 10.2.1 (assembly `Orleans.Reminders`, version `10.0.0.0`). `Orleans.Configuration.ReminderOptions` does not exist. Any design or code that says otherwise will not compile.

#### D.1.2 The minimum, and whether it can be lowered

`Orleans.Hosting.ReminderOptions` has exactly three properties:

| Property | Default |
|---|---|
| `MinimumReminderPeriod` | **`00:01:00`** |
| `RefreshReminderListPeriod` | `00:05:00` |
| `InitializationTimeout` | `00:05:00` |

Enforcement is at a **single point**, `Orleans.Runtime.ReminderRegistry.RegisterOrUpdateReminder` — client-side, inside the calling grain, before any network hop:

```csharp
TimeSpan minimumReminderPeriod = options.MinimumReminderPeriod;
if (period < minimumReminderPeriod)
    throw new ArgumentException($"Cannot register reminder {reminderName} as requested period ({period}) is less than minimum allowed reminder period ({minimumReminderPeriod})");
```

`ReminderRegistry` is a singleton that captures `options.Value` **in its constructor** — the value is a startup snapshot; there is no `IOptionsMonitor` and no hot reload. The server side (`LocalReminderService`) does not re-check the minimum; it only rejects `period <= TimeSpan.Zero`.

**It is configurable, and there is no hard floor.** `services.Configure<ReminderOptions>(o => o.MinimumReminderPeriod = TimeSpan.FromSeconds(1))` works. Notably, the only guard against an aggressive value — `Orleans.Hosting.ReminderOptionsValidator`, which warns below one minute — is **`internal` and never registered in DI in 10.2.1**. `AddReminders(IServiceCollection)` registers only `ReminderInstruments`, `LocalReminderService`, and `IReminderRegistry`. An exhaustive binary scan of every 10.2.1 package in the local cache found the validator's type name in no assembly other than `Orleans.Reminders.dll` itself. It is dead code: setting a one-second minimum produces neither an exception nor a warning.

The only real floors on a registered reminder are `period > TimeSpan.Zero` and `period != Timeout.InfiniteTimeSpan`.

#### D.1.3 Firing accuracy — the important correction

**`RefreshReminderListPeriod` does not bound firing precision in Orleans 10.2.1.** This contradicts widely-held knowledge from older Orleans versions and is the fact that decides this question.

`LocalReminderService.LocalReminderData` gives **each reminder its own in-process `System.Threading.PeriodicTimer`**, built from the silo's `TimeProvider`:

```csharp
_timer = new PeriodicTimer(_entry.Period, _shared._timeProvider);
```

The per-reminder loop does `Task.Delay(initialDueTime)` for the first tick, then `timer.WaitForNextTickAsync()` thereafter, awaiting `ReceiveReminder` inline between ticks. Therefore:

- Firing accuracy is bounded by the .NET/OS timer queue (~15.6 ms default Windows resolution), **not** by any Orleans scan interval. There is no reminder-table poll on the firing path.
- Initial due time has a **1 ms floor** (`MinimumReminderDueTime = TimeSpan.FromMilliseconds(1)`).
- First ticks are phase-anchored to `StartAt + k·Period`, not to whenever the silo loaded the reminder.
- **Ticks never overlap and missed ticks coalesce** — `ReceiveReminder` is awaited before the next wait begins, and `PeriodicTimer` collapses multiple elapsed periods into one tick. A slow `ReceiveReminder` delays subsequent ticks rather than queueing them.
- Orleans exposes a tardiness metric (`ReminderInstruments.OnTardiness`) for measuring real drift.

`RefreshReminderListPeriod` drives `ReadTableAndStartTimers`, which reconciles the local list against the global reminder table for consistent-ring ownership changes and cross-silo writes. It is **not** registration latency: `LocalReminderService.RegisterOrUpdateReminder` calls `AddOrUpdateLocalReminder` immediately after the table upsert, so a new reminder is armed at once.

#### D.1.4 Decision: reminders only, with `MinimumReminderPeriod` lowered

**A CMMN `PT15S` timer is implementable with reminders alone.** The design:

1. Configure `MinimumReminderPeriod = TimeSpan.FromSeconds(1)` explicitly in both the Development and Deployed silo paths. Today the repo configures `ReminderOptions` **nowhere** — grep returns zero hits — so both paths run at the one-minute default and a `PT15S` timer would throw `ArgumentException` at registration.
2. **One reminder per armed timer**, named deterministically: `timer:{planItemInstanceId}:{occurrenceIndex}`. Not one shared keep-alive reminder — the per-timer name is what makes reconciliation (below) possible.
3. **Non-repeating reminders.** Register with `dueTime = NextFireUtc − now`, and `period` set to a large sentinel (Orleans requires a non-infinite period). `ReceiveReminder` unregisters the reminder as its first act, then processes the tick and — if `RemainingOccurrences > 0` — registers the next one. Rationale: CMMN intervals are calendar-arithmetic-derived (§C.7c) and are not always a constant `TimeSpan`; `P1M` is not a fixed period, so a repeating Orleans reminder cannot express it. Computing each next fire explicitly is the only correct handling and costs one reminder-table write per fire.
4. **Reconciliation on activation — this replaces the outbox.** `OnActivateAsync` calls `this.GetReminders()`, diffs the live set against `CaseRuntime.Timers`, registers anything missing, and unregisters anything orphaned. Because `RegisterOrUpdateReminder` is idempotent and reminders are enumerable, this is self-healing regardless of where a crash landed relative to the journal confirm. It is also the fix for **C2** (pending timers silently lost on restart) — which under the current design is unfixable without exactly this reconciliation, because `TimerEventSchedulerGrain.OnActivateAsync` never re-reads its own durably-persisted `State.JobKeys`.
5. **Delete the 118-minute keep-alive reminder** (`TimerEventSchedulerGrain.cs:36-41`). There are zero `UnregisterReminder` calls anywhere in `src/`, so today every case that ever scheduled a timer keeps a grain re-activating every ~2 hours forever, including closed cases — unbounded reminder-table growth. With per-timer reminders, a case with no armed timers holds no reminders and is free to deactivate.

**Grain timers are rejected as the short-horizon mechanism.** `RegisterGrainTimer` has no minimum period and would give millisecond precision, and its 10.x default `Interleave = false` means the callback runs as a normal non-reentrant grain turn (the legacy `Grain.RegisterTimer` is `[Obsolete]` precisely because it always interleaved). But they are **not durable** — the XML doc is explicit: "When a grain is deactivated, all active timers are discarded" — and `KeepAlive` defaults to `false`. Adding them as an accelerator on top of reminders means two mechanisms, two sources of "did this fire?", and a durability seam exactly where C2 already lives. Not worth it for sub-15.6 ms precision that CMMN never asks for.

> **Open question OQ-8.** Two things remain unverified. (a) Reminder firing jitter under load was not measured on real hardware; ~15.6 ms is inferred from `PeriodicTimer` + `TimeProvider` semantics. (b) A one-second `MinimumReminderPeriod` against a **real Azure Table** reminder store was not tested — the code path supports it, but a high-frequency timer means one table write per fire per case, and the throughput ceiling is unmeasured. Both should be benchmarked before the constant is fixed. If Azure Table throughput turns out to be the binding constraint, the fallback is to accept a documented minimum (e.g. 5 s) and lint `timerExpression`s below it.

### D.2 `ExpressionGrain` re-entrancy

**The problem, precisely.** Today: case turn → `IExpressionGrain.ExecuteAsBool(contextRef, expr)` → `ExpressionGrain.BuildExecutable` → `GrainFactory.GetCaseFileItem(...).GetSnapshot()` (`ExpressionGrain.cs:121`). Once case-file items live inside the case grain, that third hop becomes **case grain → ExpressionGrain → case grain**. The case grain is in the call chain and is non-reentrant, so the inner call queues behind the outer turn and both block until the response timeout. Orleans' opt-in call-chain reentrancy would break the cycle, but enabling reentrancy on the case grain would destroy the single-turn atomicity this entire design rests on (§B.1) — and `StageBehavior.cs:1782-1790` already documents that #198's correctness depends on non-reentrancy today.

**Design: bind the evaluation context into the request.**

```csharp
public interface IExpressionGrain : IGrainWithGuidKey
{
    Task<ExecutableResult<bool>>    ExecuteAsBool(ExpressionRequest request);
    Task<ExecutableResult<string>>  ExecuteAsString(ExpressionRequest request);
    Task<ExecutableResult<Iso8601>> ExecuteAsIso8601(ExpressionRequest request);
}

[GenerateSerializer]
public sealed class ExpressionRequest
{
    [Id(0)] public Expression Expression   { get; init; }
    [Id(1)] public string     ContextRef   { get; init; }   // may be null
    [Id(2)] public JsonNode   ContextValue { get; init; }   // bound value of ContextRef
    [Id(3)] public JsonNode   CaseFileModel{ get; init; }   // bound when ContextRef is null — see I4
    [Id(4)] public bool       ContextMissing { get; init; } // ContextRef names an item that does not exist
}
```

The case grain resolves the binding **before** the call — the data is already in memory, so this is free. `ExpressionGrain` becomes a pure function of its request with **no `GrainFactory` use at all**.

> **I-4 (no callbacks).** `ExpressionGrain` makes no outbound grain calls. Enforce with an architecture test that fails if `Orleans.IGrainFactory` or `IClusterClient` appears in its dependency graph.

**This closes D3 entirely, not just edge A.** Edge B — `CaseFileItemGrain.EnsureCaseNotClosed` calling `ICaseGrain` on every case-file mutation (`CaseFileItemGrain.cs:290-304`) — disappears too, because case-file mutation is now a method on the case grain and the Closed check is a local field read. The distributed wait cycle has no edges left.

**What it means for `contextRef` and I4.**

**Spec, verbatim.** Table 5.32, `IfPart.contextRef` (printed p.36), and identically Table 5.54, `RepetitionRule.contextRef` (printed p.54):

> "The `caseFileItem` that serves as starting point for evaluation of the `Expression` that is specified by the condition… **If not specified, evaluation starts at the `CaseFile` object that is referenced by the `Case` as its `caseFileModel`.**"

Today `ExpressionGrain.BuildExecutable` (`ExpressionGrain.cs:116-119`) returns a bare executor with no bindings when `contextRef` is null — so the condition errors and the sentry never satisfies. That is I4: a deviation, not a simplification, and not lint-flagged.

Design:

- **`ContextRef` present** — bind the item's value under both `value` and the contextRef's own id, exactly as today (`ExpressionGrain.cs:146-148`). If the item does not exist, `ContextMissing = true` and the result is a `Failure` — fail closed, with the actionable message today's code already produces.
- **`ContextRef` absent** — bind an object whose properties are the case's existing case-file items keyed by definition id: `{ ItemA: <value>, ItemB: <value>, … }`, exposed under both `value` and the same per-item names.

Two properties worth noting:

1. **The same syntax works either way.** `ItemA.amount > 100` resolves whether `contextRef` names `ItemA` or is omitted. That is a genuinely nice outcome of binding by id in both modes.
2. **It makes multi-item conditions expressible.** [04 §I4](04-adversarial-review-2026-08.md)'s related note — "the expression context binds exactly one CFI; a condition over two CaseFileItems is inexpressible" — is closed for the `contextRef`-absent case: `ItemA.amount > ItemB.limit` now works.

One accepted inconsistency, stated rather than hidden: with `contextRef` present and missing, evaluation **fails closed**; with `contextRef` absent, a reference to a non-existent item yields `undefined` and the expression's own truthiness rules apply. The asymmetry is deliberate — an explicit `contextRef` naming a nonexistent item is a model/data error worth surfacing, whereas the caseFileModel binding legitimately contains only the items that exist (case-file items are created ad hoc, #16, not enumerated from the model).

Note that "evaluation starts at" is XPath language; there is no context node in JavaScript. Binding by name is an engine convention, as `ExpressionGrain.cs:28-42` already admits. Real XPath support (finding **I2**) remains unimplemented and orthogonal.

> **Open question OQ-9.** Should `ExpressionGrain` remain a grain at all? Once it is a pure function, it could be an injected singleton service running in-process. Keeping it a `[StatelessWorker]` releases the case grain's thread during heavy Jint work — but a StatelessWorker call still costs a local message hop plus serialization of the bound JSON, which for a small context may exceed the Jint cost outright. A policy split (in-process below a size/complexity threshold, offload above it) is a third option. `Wayfinder.Benchmarks` exists; measure before choosing. This is also the single biggest lever on turn latency (§D.6).

### D.3 Journal growth and snapshotting

#### D.3.1 What the storage actually is

Confirmed from `Program.cs:239`: `AddLogStorageBasedLogConsistencyProvider()` — **not** the state-storage variant, and not a custom provider (grep for `AddStateStorageBasedLogConsistencyProvider` / `AddCustomStorageBasedLogConsistencyProvider` across `src/` returns zero hits). All three test fixtures mirror it.

That means: **the entire event log is persisted, forever.** The view is rebuilt by replaying from event 0 on every activation. There is no snapshot, truncation, or compaction anywhere in the codebase. Because each journaled grain gets its own blob (`CaseStateContainerFactory`, `Program.cs:317-330`) and the whole blob is rewritten on each confirm, **both storage cost and per-append write cost grow linearly with history length** — today, per element; after this redesign, per case.

`[LogConsistencyProvider(ProviderName = "LogStorage")]` appears exactly twice in `src/` (on `CmmnElementGrain` and one test grain), and `[StorageProvider]` appears **zero** times. So the provider surface is a one-line change if a different consistency model is ever wanted. One outlier worth knowing: `CaseDefinitionGrain` is a `JournaledGrain` carrying **no** `[LogConsistencyProvider]` attribute, resolving whatever the unkeyed default is.

#### D.3.2 Expected growth

Today, per plan item, for the simplest possible lifecycle (create → available → enabled → active → completed), on that item's own journal:

| # | Event |
|---|---|
| 1 | `Defined` |
| 2 | `Transitioned` (Uninitialized→Available) |
| 3 | `RepetitionRuleEvaluated` (discard) |
| 4 | `RequiredRuleEvaluated` |
| 5 | `ManualActivationRuleEvaluated` |
| 6 | `Transitioned` (Available→Enabled) |
| 7 | `Transitioned` (Enabled→Active) |
| 8 | `Transitioned` (Active→Completed) |

**≈8 events per plan item, minimum** — and three of the eight are rule-evaluation audit events that fire **even when the model defines no rules at all**. `BaseBehavior.EvaluateRule` (`BaseBehavior.cs:316-355`) has no early return: `if (rule?.Condition != null)` guards only the expression *execution*; `Activator.CreateInstance<TEvent>()` + `Host.RaiseEvent(@event)` run unconditionally at line 352. Add ~1 event per satisfied criterion, +2 `Transitioned` per suspend/resume cycle, +1 per parent cascade, and a full ~8 for each repetition. On the parent stage: `ChildCreated`, sometimes `ChildTriggeredActivation`, `ChildRepeated` per repetition, plus the #198 gate's `RepetitionRequestSettled`/`RepetitionBuffered`/`RepetitionBufferDrained`.

Model: **a case with N plan items costs ~8N events**, spread today across N+1 journals and after the redesign concentrated in one.

**The redesign both increases and decreases the load, and the net is not obviously favourable:**

| Direction | Effect |
|---|---|
| ↑ | One blob per case instead of N+1. Every confirm rewrites the whole case log, so the per-append cost grows with the *case's* history, not one element's. |
| ↑ | The pinned definition (§A.5) is stored once per case — a fixed but non-trivial addition. |
| ↓ | The entire #198 repetition-gate event family is deleted (`RepetitionRequestSettled`, `RepetitionBuffered`, `RepetitionBufferDrained`, `ChildRepeated`, `Repeated`, `RepetitionRefusedWhileFailed`). |
| ↓ | Rule-evaluation audit events are only raised when a rule actually exists (§C.1). **This alone removes ~3 of the ~8 events per plan item** — roughly a 35% cut, and it is the cheapest single lever available. |
| ↓ | Activations collapse from N+1 grains to 1, so total *replay* work per case drops sharply even though one journal is longer. |
| → | Total event count is roughly unchanged; distribution changes completely. |

#### D.3.3 Is snapshotting required for this redesign?

**No — but the risk profile changes and must be stated.**

Issue **#105** ("Snapshotting + event-schema versioning", OPEN, `priority:p1`, `size:boulder`, milestone **0.3.0 — Deployed Runtime**) already owns this: *"Periodic journal snapshots to bound activation replay + an explicit event-schema/version strategy for journaled events."* It sits two milestones out; the redesign targets 0.1.0.

The redesign does not create the problem and does not make it acute:

- Cases are human/agent-paced. A case with 100 plan items and moderate repetition is on the order of 10³ events — trivial to replay.
- Cases are finite. A `Closed` case is immutable and can deactivate permanently.
- **Activation cost gets better, not worse**, in the dimension that matters: today a case view requires activating N+1 grains, each replaying its own journal, over N+1 sequential remote calls (`CaseViewProjector.CollectPlanItemsAsync`). After the redesign it is one activation, one replay, zero remote calls.

What the redesign **does** change, sharply, is the second half of #105:

> **The journal becomes the audit trail the project claims, in one totally-ordered log — which makes event-schema stability a correctness property, not a nicety.** With LogStorage-based consistency the raw events *are* the source of truth. Any change to an event class's shape breaks replay of every historical instance. Today the events are plain `[GenerateSerializer]` POCOs with no version field and no upcasting path.

**Decision D-D3-1.** Snapshotting is **deferred to #105**. Event-schema versioning is **not** deferred: the cutover (§E.3) is the last moment at which the format is free, and the convention must be established there — additive-only `[Id]` slots, no slot reuse, no type renames without an alias, and a schema-version stamp on every event. Recommend re-scoping #105 to split the two halves, with the versioning half pulled into this redesign's cutover phase.

### D.4 The public API surface

The conformance corpus drives the public surface and is the acceptance gate, so this is a compatibility design, not an afterthought.

#### D.4.1 What is actually exposed today

Six HTTP endpoints, all thin over `ISender`; **`Wayfinder.Api` makes zero direct grain calls.** Only three production call sites touch `IPlanItemGrain`/`ICaseFileItemGrain` at all (§A.4). No route addresses a plan-item instance. Two routes address a case-file item by definition id, read-only:

- `GET /api/v1/cases({caseId})/case-file-items({itemId})/history`
- `GET /api/v1/cases({caseId})/case-file-items({itemId})/versions({itemVersion})`

There is **no** HTTP create/update/replace/delete for case-file items — no such command handler exists. Every case-file mutation in the repo today comes from a test.

#### D.4.2 The new grain surface

```csharp
public interface ICaseGrain : IGrainWithGuidCompoundKey
{
    // lifecycle
    Task<CaseSnapshot> Create(string caseDefinitionId);
    Task<bool>         Defined();

    // reads — one call, no fan-out
    Task<CaseSnapshot> GetSnapshot();
    Task<IReadOnlyList<object>> GetJournaledEvents(int fromVersion, int toVersion);   // paged (§D.3)

    // commands — every one returns the POST-FIXPOINT snapshot (§F)
    Task<CaseSnapshot> Trigger(CaseWorkerTransition transition, CommandId? id = null);
    Task<CaseSnapshot> TriggerPlanItem(string planItemInstanceId, CaseWorkerTransition transition, CommandId? id = null);

    // case file — replaces ICaseFileItemGrain's 11 members
    Task<CaseSnapshot> CreateCaseFileItem(string itemId, CaseFileItem definition, JsonNode value, CommandId? id = null);
    Task<CaseSnapshot> UpdateCaseFileItem(string itemId, JsonNode value, CommandId? id = null);
    Task<CaseSnapshot> ReplaceCaseFileItem(string itemId, JsonNode value, CommandId? id = null);
    Task<CaseSnapshot> AddCaseFileItemChild(string itemId, string childItemId, CommandId? id = null);
    Task<CaseSnapshot> RemoveCaseFileItemChild(string itemId, string childItemId, CommandId? id = null);
    Task<CaseSnapshot> AddCaseFileItemReference(string itemId, string targetItemId, CommandId? id = null);
    Task<CaseSnapshot> RemoveCaseFileItemReference(string itemId, string targetItemId, CommandId? id = null);
    Task<CaseSnapshot> DeleteCaseFileItem(string itemId, CommandId? id = null);
    Task<CaseFileItemSnapshot> GetCaseFileItem(string itemId);
    Task<IReadOnlyList<CaseFileItemVersionDescriptor>> GetCaseFileItemHistory(string itemId);
    Task<JsonNode>     GetCaseFileItemValueAt(string itemId, int version);

    // planning
    Task<DiscretionaryItem[]> GetPlannableItems(string stageOrTaskInstanceId);
    Task<CaseSnapshot> PlanDiscretionaryItem(string stageInstanceId, string discretionaryItemId, CommandId? id = null);
}
```

This is the shape `ITimerEventSchedulerGrain` already uses today — case-keyed, with the plan-item instance id as a *parameter* rather than in the key. That grain survives as the template for its own replacement.

`CaseSnapshot` widens to carry the whole tree:

```csharp
public sealed class CaseSnapshot
{
    Guid CaseInstanceId; string CaseDefinitionId; PlanItemState State; Guid TenantId;
    PlanItemInstanceSnapshot[]  PlanItems;      // FLAT list; tree via ParentInstanceId
    CaseFileItemSnapshot[]      CaseFileItems;
    SentrySnapshot[]            Sentries;       // for diagnostics and for §F's assertions
    int JournalVersion;                          // the ETag D5 says the API lacks today
}

public sealed class PlanItemInstanceSnapshot
{
    string InstanceId; string ParentInstanceId; string PlanItemId; string DefinitionId;
    int Repetition; string Kind; string Name;
    PlanItemState State; PlanItemState? ParentSuspendState;
    bool Required; bool UserCompletable;
    CriterionSnapshot[] EntryCriteria; CriterionSnapshot[] ExitCriteria;
}
```

Consequences:

- **`CaseViewProjector` becomes a pure in-memory mapping with zero grain calls.** Its N+1 sequential `GetSnapshot()` walk (`CaseViewProjector.cs:47-90`) disappears. This is the single biggest performance win in the redesign.
- **`Wayfinder.Application`'s project reference to `Wayfinder.Grains`** — which exists *only* because `CaseViewProjector` needs `IPlanItemInternalGrain` — can be dropped, restoring the intended layering.
- **`PlanItemView` gains `InstanceId` and `Repetition`.** Today two repetitions of the same plan item project to two entries with the **same `Id`**, and `CaseViewProjector` then sorts by `Id`, making them indistinguishable on the wire. Pre-existing defect; fixed here.
- **`CaseSnapshot.JournalVersion` gives the API the concurrency token finding D5 says it lacks.** Torn views are no longer possible by construction (the snapshot is built inside the turn), so D5's "torn by construction" half is closed; the "no ETag" half is closed by this field.
- **Remove `casePlanModel.Id` from the grain key.** `CaseViewProjector.CaseScope` hardcodes `"CPM"` while `CaseFileItemGrain.ResolveCaseScope` *derives* it by round-tripping the definition grain. These agree only for definitions whose `casePlanModel/@id` is literally `"CPM"` — every other model is already broken through the HTTP path. See §D.5 for the replacement key.

#### D.4.3 Case-file item versioning — the one hard compatibility constraint

`GetHistory`/`GetValueAt` are HTTP-exposed and are defined today over **`JournaledGrain.RetrieveConfirmedEvents`** on the item's *own* journal. `CaseFileItemVersionDescriptor.Version`'s own remarks pin the meaning: "the Nth event `RetrieveConfirmedEvents(0, Version)` would return" — and non-value-carrying events (`ChildAdded`, `ReferenceAdded`, `Discarded`) consume sequence numbers even though they produce no descriptor. Merging every item's journal into one case journal changes those numbers' meaning outright.

**Design.** Each `CaseFileItemInstance` carries a **per-item logical version counter**, incremented on every one of its own Table 8.2 transitions (`create`, `update`, `replace`, `addChild`, `removeChild`, `addReference`, `removeReference`, `delete` — printed p.107) and stamped onto each of its journal events. `GetCaseFileItemValueAt(itemId, version)` folds the case journal filtered to that item's events up to that logical version. The route, the parameter, and the meaning ("the Nth operation on this item") are preserved.

**This must be pinned before it is changed.** The exact off-by-N relationship between today's raw journal position and a per-item operation counter — in particular whether the `CmmnElementDefined` event occupies version 1 — is not obvious from reading the code, and getting it wrong silently breaks a live HTTP contract. Phase P0 (§E.2) writes a characterization test over the current numbering; the new counter is then made to match. If it *cannot* be made to match exactly, the honest answer is a `V2` route, not a quiet change.

#### D.4.4 How the corpus adapts

The **31 `.cmmn` sample files do not change at all.** They are the spec fixtures; changing them would invalidate the gate. What changes is the harness — and the harness is the migration lever, because a large fraction of the suite already funnels through two of its methods.

```csharp
// before                                                  // after
harness.ResolveChild(caseId, ext, "PlanItemA", scope)       harness.PlanItem(deployed, "PlanItemA")
    -> IPlanItemInternalGrain                                   -> PlanItemHandle
harness.CaseFileItem(caseId, "ItemA")                       harness.CaseFileItem(deployed, "ItemA")
    -> ICaseFileItemGrain                                       -> CaseFileItemHandle
```

`PlanItemHandle` and `CaseFileItemHandle` are thin structs over `(ICaseGrain, instanceId)` exposing `Trigger(...)` / `GetSnapshot()` / `Update(...)` with the **same method names and shapes** as the grain interfaces they replace. Scenario bodies then change by one line each (the resolve) rather than being rewritten. `ResolveChildAddress` becomes `ResolveChildInstanceId` and returns an id instead of a dotted address.

Cost, honestly stated:

| Suite | Sites | Adaptation |
|---|---|---|
| Conformance scenarios (41 `[Fact]`s, 5 files) | ~35 `PollUntil` + ~10 resolves | Mechanical via the handles; plus **deleting** every `PollUntil` (§F) |
| Other integration tests (23 files, ~110 grain call sites) | ~110 | Mechanical via the handles; `CaseFileItemGrainTests.cs` alone has 24 |
| `StageBehaviorTests_*` unit tests (6 files, ~67 `Mock<IPlanItemInternalGrain>` setups) | ~67 | **Rewritten.** `StageBehavior` will operate on in-memory child objects, so these tests get *simpler* (build a real object graph, no `Mock<IGrainFactory>`), but the mocks cannot be ported. This is real, unavoidable cost and belongs in its own phase. |
| `Wayfinder.Api.Tests` (44 tests) | 0 grain calls | **Unaffected.** All mock `ISender`. |

`Conformance/COVERAGE.md` is also stale — it says "33 scenarios — 25 executed green, 8 quarantined" and describes a `[Fact(Skip=…)]` quarantine protocol, while the code has **41 scenarios and zero skips**. It should be regenerated as part of this work.

### D.5 Multi-tenancy and placement

#### D.5.1 Where tenancy lives today

Tenant is in the **key** only for definition-side grains (`ICaseDefinitionGrain`, `IPlanItemDefinitionGrain`, both `(tenantId, …)`). Every runtime grain is keyed purely by `caseInstanceId` and reads tenant from `RequestContext` via `CaseRequestContext`, whose `TenantId` getter **throws** when unset. Enforcement is `CaseStore.TenantId`, stamped at `CaseGrain.Create`, compared in `CaseGrain.GetSnapshot`/`Trigger`, throwing `CrossTenantAccessException` → mapped to **404** at the API (deliberately indistinguishable from not-found).

That fail-closed posture is *correct*, and finding **C3** is its flip side: `TimerTickJob.Execute` publishes from a Quartz worker thread with no `RequestContext`, and `PlanItemGrain.DefineRepetition` reads `CaseRequestContext.TenantId` — so a timer-driven cascade throws mid-transition, wedging a stage Active with zero children.

#### D.5.2 Design: the engine never reads ambient context

> **R-D5.** `CaseRequestContext` is read **exactly once per call**, in the command preamble, and only to authorize. Nothing in the agenda loop, in any behaviour, or in any outbound call reads it. All internal tenant needs are served by `CaseRuntime.TenantId`, pinned at `Create`.

Since the definition graph is pinned at `Create` (§A.5), the loop makes no tenant-keyed outbound calls at all, so there is nothing left that *could* need ambient tenant.

**Reminders must not repeat C3.** `ReceiveReminder` has no caller and therefore no `RequestContext` — that is not a bug to work around but the normal case:

```csharp
public Task ReceiveReminder(string reminderName, TickStatus status)
    => ExecuteTurn(
        actor: Actor.System(CaseRuntime.TenantId),       // explicit, not ambient
        seed:  () => TimerElapsed(ParseReminderName(reminderName)));
```

**`ActorStamping` changes from reading AsyncLocal to taking an explicit actor.** Today `CmmnElementGrain.RaiseEvent` shadows the base method and calls `ActorStamping.Apply(@event)`, which reads `CaseRequestContext`. In the new design the turn declares its actor at the entry point and the stamping takes it as a parameter. Consequence: **every entry point declares its actor; there is no implicit ambient read anywhere.** That is the structural fix for C3 — not "remember to stamp context at the timer boundary", but "there is no context to forget".

This requires an `ActorPrincipalType.System` member alongside today's `User` and `Client`.

#### D.5.3 The grain key

**Recommendation: `(Guid caseInstanceId, string tenantId)`.**

- The `casePlanModel.Id` disappears from the key, retiring the `"CPM"` landmine (§D.4.2).
- A foreign-tenant caller addressing case X resolves a *different* grain, which is undefined → the app layer's existing not-found path → 404. That is the same observable outcome `CrossTenantAccessException` produces today, achieved structurally instead of by a runtime comparison.
- `ReceiveReminder` can recover the tenant from `this.GetPrimaryKey(out var tenant)` even before the journal has been folded.

The runtime `TenantId` check against `CaseRuntime.TenantId` is **retained anyway** as defence in depth. A `caseInstanceId` is a Guid and globally unique, so key-tenancy adds access control, not collision safety — belt and braces are cheap.

> **Open question OQ-10.** The alternative is to keep `(caseInstanceId, casePlanModelId)` and rely purely on the persisted `TenantId` check. That is a smaller change and keeps the key's meaning tied to the model. Against it: the two current derivations of the `casePlanModelId` segment (`CaseViewProjector.CaseScope` hardcoded vs. `CaseFileItemGrain.ResolveCaseScope` derived) already disagree, so the segment is a latent bug either way and this redesign is the moment to remove it. Reviewers should confirm the key change is acceptable given it invalidates every existing local dev blob (§E.3).

#### D.5.4 Placement and working set

Default (random) placement. The case grain is the unit of work; there is nothing to co-locate with. `ExpressionGrain` is `[StatelessWorker]` and therefore always silo-local, so expression calls never cross the network regardless of where the case lands.

The genuine new consideration is **activation working-set size**: the whole case tree is resident while the grain is active. For a case with N plan items that is roughly N × (state + memoized rules + criteria + sentry occurrence maps) plus one pinned copy of the definition. For the corpus's scale (tens of plan items) this is kilobytes. For a pathological case (thousands of repetitions under the #67 ceiling) it is megabytes, and the case is *also* holding a long journal. Worth measuring; not worth designing around yet.

### D.6 Concurrency posture

#### D.6.1 What per-case serialization means

One turn at a time per case. `[Reentrant]` is **forbidden** on the case grain — enforce with an architecture test, because a future well-meaning "just add Reentrant to fix a timeout" would silently destroy §B.1's atomicity and re-open #198 with no test to catch it (which is exactly what `StageBehavior.cs:1786-1790` warns about today).

Cross-case parallelism is untouched, and that is the real concurrency dimension: cases are independent.

A read (`GetSnapshot`) queues behind an in-flight turn. That is a feature — it is what makes the returned snapshot post-fixpoint and therefore assertable (§F).

#### D.6.2 May an operation legitimately exceed a request timeout?

**Yes, plausibly.** The Orleans default response timeout is 30 s. Turn cost is dominated by expression evaluation: for N plan items with up to three rules each, worst case ~3N `ExpressionGrain` round trips, plus one IfPart evaluation per evaluable sentry per case-file event. A case-level `terminate` over a deep tree touches every instance.

Four mitigations, in order of leverage:

1. **Memoize expression results within a turn**, keyed by `(expression body, contextRef, case-file logical version)`. Sound under invariant I-2 (§B.4.1). With per-criterion sentry instances (D-C8-1) this recovers most of the duplicate-evaluation cost that decision introduces.
2. **Honour the spec's own memoization.** Table 8.8 says ManualActivationRule and RequiredRule values "SHOULD be maintained for the rest of the life of the … instance". Re-evaluating them is not merely wasteful, it is non-conforming (§C.1).
3. **Bound the turn.** `MaxOperationsPerTurn` (§B.4.4) must be sized so that `budget × per-op cost < ResponseTimeout`. That arithmetic must be written down and checkable, not assumed (OQ-2).
4. **Set `SiloMessagingOptions.ResponseTimeout` explicitly** rather than inheriting the default, so the relationship between the budget and the timeout is visible in one place.

**A caller-side timeout does not roll back the turn.** The grain keeps working and confirms. Therefore:

#### D.6.3 Idempotency is a new, required property

If a client retries a command after a timeout, the command must not double-apply. Design: every mutating method takes an optional caller-supplied `CommandId`; `CommandReceived` records it; the preamble rejects a duplicate by returning the snapshot produced by the original turn. A bounded recent-command-id ring is kept in state (say the last 64), which is sufficient for retry windows and does not grow.

This is a genuinely new requirement that the redesign introduces — today's `Trigger` returns before propagation settles, so "did it apply?" was already unanswerable and nobody retried. Making the call meaningful makes retry meaningful too.

#### D.6.4 How the loop avoids blocking on outbound calls

Three rules, all stated earlier, collected here because together they are the answer:

1. **`ExpressionGrain` is the only outbound call the loop makes** (§A.5 removes the definition grain; §D.2 removes the case-file-item grain; §D.1 makes reminder registration a post-confirm step).
2. **It cannot call back** (invariant I-4, §D.2), so there is no cycle and no reentrancy requirement.
3. **Awaits happen only in `Prepare`, never inside a Stateless entry/exit action** (§B.3). The runtime is never torn across an await, and transitions become fully synchronous.

---

## E. Migration and sequencing

### E.1 There is nothing to migrate — confirmed from the repo

Verified, not taken on faith:

| Check | Result |
|---|---|
| IaC (bicep / terraform / ARM) | **None.** Only `devops/infrastructure/bicepconfig.json` — an analyzer config with no templates to analyze. `devops/infrastructure/README.md:56` says "## Bicep (reserved)". |
| Deploy steps in any workflow | **None.** `.github/workflows/cd.yml:6-9` states outright: *"No deploy-to-Azure steps here - there is no Azure environment to deploy to yet…"* |
| `devops/deploy/templates/` | Empty except a README, which marks the CD template "(planned, roadmap M2 #49/#34)". |
| Real Azure endpoints or credentials | **None.** Every connection string in the repo is Azurite's public `devstoreaccount1` key. `src/Wayfinder.Silo/appsettings.json` ships `"Azure": { "Storage": {}, "Clustering": {} }` — a deliberate fail-fast placeholder. |
| Release tags | **Zero.** `git tag` is empty; `gh release list` is empty. |
| `CHANGELOG.md` | One section: `## [Unreleased]`. |
| `GitVersion.yml:5-6` | *"Baseline: 1.0.0. No release tags exist yet"* — all builds are 1.0.0 previews. |

The only durable data anywhere is a developer's local Azurite volume (`azurite-data`) or eval-stack volume, both explicitly disposable via `docker compose down -v`. Container images from ~10 successful CD runs likely exist in ghcr.io — a build artifact, not a running deployment, and there is no infrastructure to run them against. (This is the one item that could not be enumerated directly: the packages API returned 403 for lack of `read:packages` scope.)

### E.2 What that means for journal-format compatibility

**The new journal format is greenfield.** No upcasting, no dual-read, no migration tooling, no compatibility shims. Event shapes, `[Id]` slots, type names, and the grain key may all change freely.

Three obligations follow, and they are not optional:

1. **Wipe instructions.** Every developer and CI environment holding an Azurite volume must drop it at cutover, because the old and new formats share type names but not shapes and a partial fold would silently produce a wrong state rather than an error. Document the exact commands (`docker compose -f devops/infrastructure/docker-compose.yml down -v`, same for `devops/eval/`).
2. **The key change invalidates addresses too.** Changing the `ICaseGrain` key extension (§D.5.3) means old blobs are unreachable even if their format matched. That is fine now and impossible later.
3. **This is the last free moment.** From cutover onward the journal *is* the audit trail, and format changes become breaking. The event-schema-versioning convention (§D.3.3) must land **in** the cutover, not after it.

### E.3 Phases

Being honest about what is and is not independently shippable matters more than a tidy phase list.

**P0 — Characterization (independently shippable).**
Pin current observable behavior as tests, on the *old* engine, before touching anything: the 41 conformance scenarios' final states, the case-file item version numbering (§D.4.3), and the current `R<n>` occurrence count (§C.7a) so the off-by-one change is visible rather than silent. Precedent: commit `4751ff2`, "#216 phase 1, tests only". **Verifiable:** green on the old engine.

**P1 — Decouple expressions and pin definitions (independently shippable).**
Change `IExpressionGrain` to the bound-request contract (§D.2) including I4; resolve and pin the definition graph at `Create` (§A.5). No topology change; both engines would want these. **Verifiable:** corpus still green; architecture test proves `ExpressionGrain` has no outbound grain calls.

**P2–P7 — the collapse. Not independently shippable, and this document will not pretend otherwise.**
From the corpus's point of view the collapse is one atomic replacement: you cannot half-collapse a case. What *is* achievable is reviewability. The new `CaseGrain` is built on a long-lived integration branch, **dark** (implemented and unit-tested, but not registered in the silo and not resolved by any test), as a sequence of small PRs, each with its own tests, ending in one cutover PR.

| | Scope | Its own verification |
|---|---|---|
| P2 | Object model, agenda loop (§B), Tables 8.6/8.8/8.9/8.11 cascades, Table 8.12 (§C.2), **M1, M2, M4, M5** | Unit tests over the runtime object graph; a scenario runner that drives `.cmmn` samples through the new engine directly, in-process |
| P3 | Sentry algebra (§C.8: **C4, M8, M9, M10, I7**), **M6a/b**, cross-stage + bottom-up (**M11**) | The sentry-focused corpus subset, run against the dark engine |
| P4 | Case file inside the case grain, incl. version-number compatibility (§D.4.3) | P0's characterization test, re-pointed |
| P5 | Timers on reminders (**M7a/b/c**, **M6c**); `MinimumReminderPeriod` config (§D.1) | Timer scenarios via the injectable clock (§F.3) |
| P6 | Planning table, roles, `GetPlannableItems`, `PlanDiscretionaryItem` | Discretionary corpus scenarios |
| P7 | **Cutover.** Register the new grain; delete `PlanItemGrain`, `SentryGrain`, `CaseFileItemGrain`, `PlanningTableGrain`, `RoleGrain`, `CmmnElementGrain`, `Scheduler/`, `Infrastructure/Quartz/`, all stream types and `StreamFlags`; adapt the corpus harness (§D.4.4); **delete every wait helper (§F.4)**; land the event-schema-versioning convention (§E.2). | **The whole corpus, deterministically, N consecutive runs** |

**P8 — API surface (independently shippable after P7).**
Widen `CaseSnapshot`; simplify `CaseViewProjector` to a pure mapping; drop `Wayfinder.Application` → `Wayfinder.Grains`; add `InstanceId`/`Repetition` to `PlanItemView`; narrow the wire transition enum (§C.5); regenerate `COVERAGE.md`.

**P9 — Deferred.** Journal snapshotting → #105 (§D.3.3).

> **Open question OQ-11.** The dark-branch approach costs a long-lived branch and a large final PR. The alternative — registering both engines behind a per-case feature flag and migrating the corpus scenario by scenario — is more incremental but requires both engines to coexist in one silo, with two `ICaseGrain` implementations, doubled fixtures, and a real risk of the flag outliving the migration. This design recommends the dark branch on the grounds that there is no production traffic to protect (§E.1), which is the usual reason to pay for a flag. Reviewers should push back if the branch is expected to live more than a few weeks.

---

## F. Acceptance criteria

**The redesign is done when the conformance corpus — 31 `.cmmn` samples driving 41 scenarios — passes deterministically with every polling and scaled-wait helper deleted.**

### F.1 What "deterministically" means operationally

Three properties, each of which must be true for the phrase to mean anything:

> **F-1.** Every mutating grain call returns only after the agenda has drained to fixpoint **and** the journal has been confirmed. (§B.1 steps 4–5.)
>
> **F-2.** A test therefore asserts on the **returned snapshot**, immediately. No polling, no delay, no timeout, no retry. If a state is not in the returned snapshot, the engine will not produce it in response to that call — ever.
>
> **F-3.** A "must NOT happen" assertion is a plain assertion on the returned snapshot. This is the property that matters most, because it is the one the current architecture cannot provide at any price.

F-3 deserves the emphasis. Today's corpus contains this, verbatim, in `LifecycleScenarios.cs:577-583`:

> ```
> // No clean positive upper bound exists for "the CasePlanModel has finished NOT
> // spawning" - ConformanceHarness.PollUntil's documented 30s budget stands in,
> // generous enough that a real (bug-reintroducing) spawn's handful of in-process
> // awaits would trivially complete inside it on any engine, fixed or not.
> ```

That comment is an honest admission that the negative assertion is unfalsifiable: it passes if the bug is absent *and* if the bug is present but slow. After the redesign the assertion becomes `snapshot.PlanItems.Count(p => p.PlanItemId == "MilestonePlanItem").Should().Be(1)` — total, immediate, and falsifiable.

### F.2 What a test asserts, and when

```csharp
// Before
var terminated = await ConformanceHarness.PollUntil(
    async () => (await taskGrain.GetSnapshot()).PlanItemState == PlanItemState.Terminated);
terminated.Should().BeTrue("Table 8.8 (exit): …");

// After
var after = await caseFile.Update(JsonNode.Parse("""{"abort": true}"""));   // returns CaseSnapshot
after.PlanItem("PlanItemA").State.Should().Be(PlanItemState.Terminated, "Table 8.8 (exit): …");
```

The mutating call itself carries the assertion point. There is no second round trip and no window.

Two stronger checks become available and should be used:

- **Journal-sequence golden tests.** Because the journal is totally ordered, a scenario's effect-event sequence is a deterministic function of its inputs. For a handful of representative scenarios (one cascade, one repetition, one sentry AND-join), assert the exact sequence. This is the strongest determinism check available and would have caught every ordering bug in the #161/#177/#181/#198 lineage.
- **Repeat runs.** The corpus must pass **N consecutive times** (suggest 50) in CI with zero failures before the redesign is declared done. If any scenario flakes once, the fixpoint claim is false somewhere.

### F.3 The one place determinism needs machinery, not just the fixpoint

**Timer-driven scenarios.** A reminder is by definition a *separate* turn, arriving on wall-clock time. No fixpoint makes that deterministic.

Design: an `ICaseClock` / timer-driver seam. In production, `ReceiveReminder` drives it. In tests, the harness calls an internal `AdvanceTimersTo(instant)` / `FireTimer(timerInstanceId)` surface directly, which runs exactly the same turn the reminder would have. Then even timer scenarios assert synchronously, and `TimerEventSchedulerGrainTests`' current 2-second "extra ticks must not arrive" sleep becomes a real assertion.

This seam must be designed in P5, not bolted on at P7. Without it, timers are the one place the acceptance gate silently keeps a scaled wait.

### F.4 Every helper that exists only to paper over asynchronous reconstruction

These are the measurable proof. All of them are deleted at P7. The inventory is exhaustive:

**Deleted outright:**

| Helper | Location | Sites |
|---|---|---|
| `JournalPolling.UntilJournaled<TEvent>` | `Wayfinder.Grains.Tests.Utils/Helpers/JournalPolling.cs` (whole file) | 3 (`SentryGrainTests.cs:198`, `CaseFileItemSentryIntegrationTests.cs:421,522`) |
| `ConformanceHarness.PollUntil` + `DefaultTimeout = 30s` | `Conformance/ConformanceHarness.cs:222,40` | **35** across all 5 scenario files. The 30 s constant is the #149 widening. |
| `StageCompletionCascadeIntegrationTests.PollUntil` | `:477` | 9 |
| `RepetitionAfterTerminationIntegrationTests.PollUntil` / `PollUntilGrainFound` | `:345`, `:332` | 6 |
| `StageCompletionRulesIntegrationTests.PollUntil` | `:338` | 5 |
| `CaseFileItemSentryIntegrationTests.PollUntil` | `:530` | 4 |
| `SentryRepetitionResetIntegrationTests.PollUntil` / `PollUntilGrainFound` | `:269`, `:256` | 3 |
| `RepetitionCompletionRaceIntegrationTests.PollUntil` (hardcoded 10 s) | `:437` | 3 |
| `TimerEventSchedulerGrainTests.WaitUntilAsync` | `:169` | 3 |
| `CmmnImportDeployIntegrationTests.PollUntil` | `:137` | 1 |
| `CaseLifecycleIntegrationTests.PollUntil` | `:243` | 1 |
| `CasePlanModelInstantiationIntegrationTests.PollUntil` | `:394` | 1 |
| `RepetitionOnCompletionIntegrationTests.PollUntilGrainFound` | `:205` | 1 |
| `RepetitionRedeliveryIntegrationTests.PollUntilChildCount` | `:181` | 1 |
| `RepetitionGuardFootgunIntegrationTests.PollUntilCaseState` | `:116` | 1 |

**14 helper definitions, ~71 call sites.**

| Idiom | Sites | Notes |
|---|---|---|
| Bare `await Task.Delay(500ms)` as a settle window before a negative assertion | **11** — `CaseFileScenarios.cs:46,76`; `SentryScenarios.cs:54,87,125`; `SentryGrainTests.cs:324,383,437,450`; `CaseFileItemSentryIntegrationTests.cs:231`; `CasePlanModelInstantiationIntegrationTests.cs:187`; `CmmnImportDeployIntegrationTests.cs:95`; `RepetitionOnCompletionIntegrationTests.cs:145`; `RepetitionGuardFootgunIntegrationTests.cs:110`; `BaseEventTests.cs:57` (50 ms); `TimerEventSchedulerGrainTests.cs:85` (2 s) | Five of these are **inside the conformance corpus itself** |
| `Task.WhenAny(tcs.Task, Task.Delay(...))` stream-arrival deadlines | **15** — `SentryGrainTests.cs` ×7, `CaseFileItemGrainTests.cs` ×3, `BaseEventTests.cs`, `StreamSemanticsTests.cs` ×3, `AzuriteJournalStorageTests.cs` | Wait on stream deliveries that will not exist |
| Bespoke quiet-window loops | **3** — `RepetitionRedeliveryIntegrationTests.cs:161-167`, `QuartzSchedulerInstanceNameCollisionTests.cs:164-168`, `TimerEventListenerEndToEndTests.cs:100` | |
| `RepetitionGuardClusterFixture.WarmUpRepetitionCascadeAsync` | `:98` — a full spawn→complete→respawn→breach→fault cascade against a throwaway case with a 30 s deadline, swallowing all exceptions, purely to amortize first-hit cost. Attributed to #153. | Deleted |
| `xunit.runner.json` `"parallelizeTestCollections": false` | Justified in the csproj as the #153/#154 CPU-oversubscription fix | **Candidate for re-enabling** — see below |

**Whole test classes that lose their subject.** These exist to test transport and have nothing to test afterwards: `StreamSemanticsTests`, `RepetitionRedeliveryIntegrationTests` (the #161 redelivery guard), `QuartzSchedulerInstanceNameCollisionTests` (#197), `TimerEventSchedulerGrainTests`, `StageBehaviorTests_CreateChild_SubscribeOrdering` (#181). Their *intent* — "a repetition is not lost", "a timer fires once" — must be re-expressed as engine-level scenarios before they are deleted, not silently dropped with them.

**A stretch goal worth stating.** `parallelizeTestCollections: false` was set because parallel TestCluster fixtures oversubscribed CI CPUs and caused #153/#154. With streams gone and turns synchronous, that pressure should largely disappear. Re-enabling collection parallelism after P7 would be strong independent evidence that the flakiness was structural rather than environmental — and it would make the suite substantially faster. Treat it as a signal, not a requirement.

### F.5 The gate, stated as a checklist

1. All 41 conformance scenarios green, 50 consecutive CI runs, zero flakes.
2. `grep -r "PollUntil\|JournalPolling\|Task.Delay\|WaitUntilAsync" src/Wayfinder.Grains.Tests.Integration/` returns nothing outside the timer-driver seam.
3. `Wayfinder.Grains.Tests.Utils/Helpers/JournalPolling.cs` does not exist.
4. Journal-sequence golden tests pass for the three representative scenarios.
5. Architecture tests pass: case grain is not `[Reentrant]`; `ExpressionGrain` has no outbound grain calls; no `IAsyncStream` type is referenced from `Wayfinder.Grains`.
6. The `KnownGapScenarios` quarantine narrative is resolved — every gap it documents is either fixed or restated as an explicit, cited decision.

---

## G. What is preserved

Per [04 §10](04-adversarial-review-2026-08.md).

### G.1 Carries over untouched, or near enough

| Asset | Status |
|---|---|
| **The three Stateless lifecycle configurations** (`PlanItemStateMachine.ConfigureForCasePlanModel` / `ForStageOrTask` / `ForMilestoneOrEventListener`) — near cell-perfect against Tables 8.6/8.8/8.11, including fault non-propagation, exit-from-Failed, and the CasePlanModel terminating via `terminate` not `exit` | **Preserved verbatim.** One deletion: `RetainSynchronizationContext = true` and the `FiringMode.Queued` reentrancy dependence (`PlanItemStateMachine.cs:36-89`, closing #64) become unnecessary once rule evaluation moves out of entry actions (§B.3). A simplification, not a semantic change — but it *is* a change to that file, so it is called out. |
| **Table 8.9's per-type cascade asymmetries** in both directions (Milestone/EventListener children surviving a completed parent; `fault` not propagating) | **Preserved**, and consolidated from four diverging switch ladders into one table (§C.4). |
| **Behaviour-rule defaults on every path including error paths** (the #158 class), incl. first-evaluation-discard for repetition | **Preserved verbatim.** One change: no audit event when no rule exists (§C.1, §D.3.2). |
| **The behaviour layer's semantics** — `IBehaviorHost`, `StageBehavior`, `TaskBehavior`, `MilestoneBehavior`, `EventListenerBehavior`, `TimerEventListenerBehavior`, `CasePlanModelBehavior` | **Rules preserved; plumbing deleted.** `IBehaviorHost` survives in shape but its meaning changes from "the grain hosting me" to "the case runtime hosting me". `StageBehavior` at 1,833 lines should land in the low hundreds once the buffering, the #198 gate, `DrainPendingRepetitions`, `SettleStrandedPendingRepetitions`, and the subscription machinery are gone. |
| **The event model** (`Plan/**/Events/`, ~39 journaled types) | **Largely preserved**, with one structural change: events formerly raised on a plan item's own journal now go on the case journal and must carry a plan-item instance id. That is a field addition to nearly every type. `ActorStamping` is preserved but takes an explicit actor (§D.5.2). |
| **The interchange layer** — `CmmnXmlSerializer`, `Spec.CMMN.MODEL.cs`, the XmlSerializer collection-shadow mechanism with its reflection coverage test, DTD/XXE-hardened import, XSD optional-attribute defaults, `CmmnCapabilityLint` | **Untouched.** Findings I1–I6 remain orthogonal and unscheduled. Only I7's OnPart predicate is in scope (§C.8), and it is a runtime predicate, not interchange. |
| **The conformance corpus** — 31 `.cmmn` files and their `ConformanceCitationAttribute` spec citations | **Untouched.** Only the harness changes (§D.4.4). |
| **CaseFileItem lifecycle fidelity** — all eight Table 8.2 transitions, Available-only guards, Closed lockdown | **Preserved.** `EnsureCaseNotClosed`'s cross-grain call becomes a local field read. |
| **`sentryRef` / `exitCriterionRef` OnParts end-to-end** (#82's D10 work) | **Preserved.** The Stateless parameterized-`Exit` mechanism that threads the criterion id becomes unnecessary — the agenda operation carries it directly. Simplification, not loss. |
| **Sandboxed Jint with honest error paths**; the IfPart fault-vs-FALSE distinction (`IfPartResult`) | **Preserved**, and the fault/FALSE distinction becomes load-bearing in the new algebra (§C.8 step 3). |
| **Orleans as substrate** — hosting, placement, `JournaledGrain`, reminders, compound-key multi-tenancy, `TestCluster` | **Preserved.** |
| **The stack** — Stateless 5.20.1, Jint 4.11.0, NodaTime, Serilog, the hand-rolled `ISender` seam, xUnit/Testcontainers-Azurite/FluentAssertions 7 | **Preserved.** Quartz 3.18.2 is the sole removal. |
| **Test architecture shape** (§8's R5) | **Preserved.** The corpus still drives the public surface. Only the wait helpers go. |
| **Code comments that state their ordering assumptions and residual windows honestly** | **Preserved as a practice** — the comments themselves mostly describe machinery that no longer exists, but the discipline is the thing worth keeping, and this document is written in the same register. |

### G.2 Deliberately rewritten

`CmmnElementGrain.SubscribeTo`/`UnsubscribeFrom`/`PublishEvent`/`GetCaseEventStream`, `StreamFlags` and the Create/Resume contract, `SentryStore`'s occurrence-token model, `StageBehaviorStore`'s buffering fields, `PlanItemStore.Apply(ParentSuspended)`, the four `HandleParentTransitioned` implementations, `CaseViewProjector`'s traversal, and `Iso8601`'s duration/instant parsing.

### G.3 What cannot survive — findings

Three items [04 §10](04-adversarial-review-2026-08.md) records as worth preserving do not survive as-is. Stating them loudly, as instructed.

**1. The #198 blocking/clearing-signal design is deleted, not ported.**

§10 records it as "sound against interleaving, and the pattern worth keeping where cross-grain edges remain". After this redesign **there are no cross-grain edges inside a case**, so the qualifier is satisfied vacuously and the mechanism has nothing to defend against. More importantly, the *interval* it exists to cover ceases to exist: the repetition determination and the successor's creation happen in the same turn, in the same journal write, with the agenda's FIFO-before-deferred ordering (§B.2) guaranteeing the instantiation runs before the completion check.

What must be preserved is the **invariant**, not the mechanism:

> No stage completes over a determined-but-uncreated successor.

Enforced now by ordering rather than by a durable blocking flag. Deleted with it: `RepetitionRequestsAwaitingResolution`, `SettleRepetitionRequest`, `TryCompleteStageAfterRepetitionResolved`, `RepetitionRequestSettled`, `RepetitionBuffered`, `RepetitionBufferDrained`, `DrainPendingRepetitions`, `SettleStrandedPendingRepetitions`, and `StageBehaviorStore._settledRepetitionSourceInstanceIds`.

This should be reviewed carefully. It is ~600 lines of hard-won, well-tested logic being deleted on the argument that its precondition no longer holds. If that argument is wrong anywhere, this is where the redesign breaks.

**2. `CmmnElementGrain` dissolves as an abstraction.**

It is the shared base for `CaseGrain`, `PlanItemGrain`, `CaseFileItemGrain`, `SentryGrain`, `PlanningTableGrain`, and `RoleGrain`, and it carries the single `[LogConsistencyProvider]` attribute, the `RaiseEvent` actor-stamping shadow (#59), the `GetJournaledEvents` seam, the dotted-address decomposition, and the whole subscription contract. Only the case remains a grain, so the base class has one subclass and no reason to exist. Its useful parts — actor stamping, the journal read seam — move onto the case grain directly, and `GetJournaledEvents()` must gain paging (§D.4.2), because "return the entire journal in one call" does not survive a single merged log.

**3. Per-element journals, and everything read through them.**

`ActorStampingIntegrationTests` and `CaseFileItemGrainTests` read a specific element's journal via `GetJournaledEvents()`. There is now one journal. Those tests must be re-expressed as filtered reads over the case journal. This is the mechanism behind §D.4.3's versioning constraint and is the only place the merge is user-visible.

### G.4 Two smaller preservation notes

- **`PlanItemSnapshot.BehaviorExtension` as `object`, with `StageBehaviorSnapshot.Children` (`planItemId → instanceId → repetition`) as the sole instance registry**, is replaced by a real tree on the snapshot (§D.4.2). The `is StageBehaviorSnapshot` type test that currently detects stage-ness goes with it.
- **`ITimerEventSchedulerGrain` is deleted but is the template for its own replacement**: case-keyed, plan-item instance id as a parameter. That shape is exactly what §D.4.2 adopts for the whole collapsed surface.

---

## H. Open questions, collected

Each is unresolved on purpose. Reviewers should treat these as the attack surface.

| # | § | Question | Options | Leaning |
|---|---|---|---|---|
| **OQ-1** | B.4.4 | What happens when the agenda does not converge? | (a) Fault the case and confirm — auditable, recoverable via `re-activate`, but the partial cascade becomes durable. (b) Abort without confirming — case stays clean, but the divergence needs a separate diagnostics sink. | (a) |
| **OQ-2** | B.4.4 | What are `MaxOperationsPerTurn` and `MaxSignatureRepeats`? | Must satisfy `budget × per-op cost < ResponseTimeout`; per-op cost is unmeasured. | Benchmark before fixing |
| **OQ-3** | C.2 | Does a Suspended non-required child block `autoComplete`? | (a) No — complete, and Table 8.9's cascade exits it. (b) Yes — add a conjunct Table 8.12 does not state. | (a) |
| **OQ-4** | C.3 | Re-activating a Failed child under a **Completed** parent — no Table 8.9 row exists. | (i) Refuse. (ii) Allow then `exit`. (iii) Allow the impossible configuration. And: guard at the transition or only at the public surface? | (i), at the transition |
| **OQ-5** | C.6 | A repeating timer suspended across several periods. | (α) One catch-up occurrence, re-anchor. (β) Back-fill N. (γ) Skip. Per-model attribute instead of engine policy? | (α) |
| **OQ-6** | C.7c | Time zone for calendar duration arithmetic and offset-less instants. | (i) Reject (rejects conforming input). (ii) Assume UTC + lint warning. (iii) Per-tenant zone. | (ii) now, (iii) later |
| **OQ-7** | C.8 | Sentry instance per **criterion instance**, or shared per owning-stage instance? | Per-criterion: fresh state per repetition (kills #177), local evaluability gate; costs duplicate IfPart evaluation. Shared: cheaper; reintroduces "satisfied for whom?". | Per-criterion |
| **OQ-8** | D.1.4 | Reminder jitter under load, and Azure Table throughput at a 1 s `MinimumReminderPeriod`. | Both unmeasured. Fallback is a documented minimum (e.g. 5 s) plus a lint rule on `timerExpression`s below it. | Benchmark |
| **OQ-9** | D.2 | Should `ExpressionGrain` stay a grain once it is a pure function? | (a) `[StatelessWorker]` — releases the case thread; costs a local hop + JSON serialization. (b) In-process service — no hop; blocks the turn. (c) Threshold policy. | Measure |
| **OQ-10** | D.5.3 | Grain key: `(caseInstanceId, tenantId)` or keep `(caseInstanceId, casePlanModelId)`? | Tenant-in-key makes cross-tenant access structurally impossible and retires the `"CPM"` landmine; invalidates every existing local blob. | Tenant-in-key |
| **OQ-11** | E.3 | Dark long-lived branch, or both engines behind a per-case feature flag? | Dark branch: one big final PR, no flag debt. Flag: incremental, but two `ICaseGrain` impls in one silo and a flag that may outlive the migration. | Dark branch |
| **OQ-12** | D.3.3 | Should #105 be split? | Snapshotting is genuinely deferrable to 0.3.0. Event-schema versioning is **not** — cutover is the last free moment. | Split; pull versioning into P7 |

---

## Appendix — spec citation index

Every normative claim above, with its printed page. PDF page = printed + 18.

| Citation | Printed | Used in |
|---|---|---|
| Table 5.19 — TimerEventListener attributes (`timerExpression`, `timerStart`) | 26 | C.7a |
| Table 5.21 — PlanItemStartTrigger attributes | 27 | C.7a |
| Table 5.26 — Sentry attributes ("MUST have an IfPart or at least one OnPart") | 33 | C.8 |
| Table 5.28 — CaseFileItemOnPart (`sourceRef : CaseFileItem[1]`) | 34 | C.8/I7 |
| Table 5.29 — CaseFileItemTransition enumeration (8 values, 6 repeatable) | 34 | C.8/M9 |
| Table 5.30 — PlanItemOnPart (`sourceRef [0..1]`, `exitCriterionRef`, "When sentryRef is specified, standardEvent MUST have value 'exit'") | 35 | C.8/I7 |
| Table 5.31 — PlanItemTransition enumeration (17 values; no `parentTerminate`) | 35-36 | C.5/I3 |
| Table 5.32 — IfPart attributes (`contextRef` default = caseFileModel) | 36 | D.2/I4 |
| Table 5.33 — Expression attributes (`language` default) | 36-37 | G.1 (I2, orthogonal) |
| §5.4.11.3 / Table 5.54 — RepetitionRule (`contextRef` default; "EventListeners cannot have RepetitionRule … R\<n\>/ notation") | 54 | C.7b |
| Table 5.55 — Applicability of PlanItemControl rules (EventListener: N/A) | 56 | C.7b |
| §8.3 / Tables 8.1-8.2 — CaseFileItem lifecycle (8 transitions) | 107 | D.4.3 |
| Table 8.6 — Case instance transitions (`re-activate`, `close` actors) | 113 | C.5 |
| Figure 8.3 — Stage/Task lifecycle, history pseudo-state | 113 | A.2, B.5.2 |
| Table 8.7 — Stage/Task states; **bottom-up activation** | 114 | C.11 |
| Table 8.8 — `create`, `enable` (**RequiredRule MUST re-evaluate**), `start`, `disabled`, `manual start`, `suspended`, `fault` | 115 | C.1, C.5, B.4.2 |
| Table 8.8 — `complete`, `terminate` (repetition re-evaluation), `exit`, `resume`, `re-activated`, `re-enable` (**RequiredRule MUST re-evaluate**) | 116 | C.1, C.5, C.11 |
| Table 8.8 — `parent suspend`, `parent resume`; Table 8.9 begins | 117 | C.3, C.6 |
| Table 8.9 — `suspend`, `fault` rows | 118 | C.4 |
| Table 8.9 — `complete` rows (`<impossible>` cells), `exit`/`terminate` rows, **Notes (1) and (2)** | 119 | C.2, C.3, C.4 |
| Tables 8.10-8.11 — EventListener/Milestone states and transitions; `create` (RequiredRule for Milestone) | 120 | C.1, C.5 |
| Table 8.11 — `suspend`, `terminate`, **`occur`** (UserEventListener vs Milestone), `resume`, `parent terminate`; **§8.5 Sentry** | 121 | C.5, C.6, C.8, C.9 |
| Table 8.12 — Stage completion criteria; §8.6.1 ("SHOULD complete"); §8.6.2 ManualActivationRule; §8.6.3 RequiredRule; §8.6.4 RepetitionRule | 122 | C.1, C.2 |
| §8.6.4 Examples 1 and 2 (B/B′/B″ → one A) | 123 | C.11, B.4.3 |
| §8.6.5 ApplicabilityRule; §8.7 Planning / Table 8.13 | 124 | P6 |
