# Case-Grain Granularity Redesign — Design

**Status:** draft for review. No implementation issues have been cut.
**Decision being designed:** D-2026-08-01 (see [04 §9](04-adversarial-review-2026-08.md)). The decision itself is not re-litigated here.
**Base:** `github/develop` @ `4751ff2`, plus PR #236's review document.
**Audience:** the implementer who will build this, and the reviewer whose job is to find the holes.

> **Spec citations.** All CMMN citations are to OMG CMMN 1.1 (`formal-16-12-01`), by **printed** page. The PDF is not in this repo. **Printed page = PDF page − 18.** Every normative claim in §C and §D below was read from the PDF directly, not from [03](03-cmmn-execution-semantics.md). Where this document disagrees with [03](03-cmmn-execution-semantics.md), the PDF wins and the disagreement is called out.

---

## 0. What this document decides, and what it leaves open

Open questions are recorded inline and collected in [§H](#h-open-questions-collected), sorted by **what has to happen to them**: five are **blocking** and must be answered before the first implementation issue is cut (OQ-11, OQ-7, OQ-3, OQ-10, OQ-6); five are decisions already taken with their dissent kept on record and are safely deferrable; two turned out not to be questions at all, but unmeasured constants with the benchmark harness already in the repo.

**Revision note (this pass).** Two independent reviews — one adversarial-design, one spec-conformance — returned FIX-THEN-SHIP. The direction and the spec research survived; a set of *inferences drawn from correctly-quoted text* did not. The corrections are marked in place throughout rather than silently applied, because each one is a place a careful reader would otherwise repeat the same mistake. The load-bearing ones: repetition instances are now **started**, not merely created (§C.6(a)) — the earlier draft structurally re-created #177 while claiming to close it; the **monotonicity theorem is withdrawn** and replaced with a stated bound and a specified fault (§B.4); the `Disabled`/terminal repetition refusal is **removed** as unsupported (§C.6(b)); the §8.5 evaluability gate now has an **insertion point in the algebra** rather than only an assertion (§C.8 step 2, §C.9); `complete` is **removed from the case-worker allowlist** except for HumanTask (§C.5); and §E.3's "not incrementally shippable" is **retracted** as overstated (three seams, §E.3).

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
    CriterionKind                   Kind;            // Entry | Exit — drives the §C.9 evaluability gate
    Dictionary<string, Occurrence>  Pending;         // OnPart.Id -> most recent unconsumed occurrence
    long                            SatisfactionCount;  // discrete satisfactions; NOT a monotonic bool (§C.8)
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
| `IExpressionGrain` | **Kept** as `[StatelessWorker]`, **contract rewritten** (§D.2). | **A constant** (`Guid.Empty`) — see below |
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

> **Step 6 runs after *every* turn, not only on activation.** An earlier draft placed reminder reconciliation in `OnActivateAsync` alone. That is wrong: the grain **stays activated** between turns, so a step-6 failure would go uncorrected until the next deactivation — possibly hours, possibly never for a busy case. Worse, `CommandId` idempotency (§D.6.3) makes a client retry return *early*, before step 6, so the obvious recovery path does not reach it either. Reconciliation therefore runs at the end of every turn **and** on activation, and both paths are the same idempotent diff (§D.1.4).

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
| 3 | `ConsumeSatisfaction(criterion, satisfaction)` | ManualActivationRule, RepetitionRule | Applies §8.5's criterion semantics to the owning instance: enable/start, exit, occur, or spawn a repetition **carrying this satisfaction forward** (§C.6); or defers if suspended |
| 4 | `Instantiate(stage, planItemModel, repetition, forwardedSatisfaction?)` | RequiredRule, RepetitionRule (discarded on first) | Creates the instance, arms its criteria, fires `create`; **if `forwardedSatisfaction` is present, enqueues `ConsumeSatisfaction` against the new instance's matching criterion**; enqueues `EvaluateCompletion(stage)` |
| 5 | `EvaluateCompletion(stage)` | RequiredRule is memoized — no evaluation | Table 8.12 (§C.2); may enqueue `Transition(stage, complete)` |
| 6 | `PropagateContainment(instance)` | — | Applies Table 8.9's containment invariant after a transition that could violate it (§C.4) |

**The two-phase split is load-bearing.** Rule and IfPart evaluation requires an outbound call to `ExpressionGrain`. Confining every such call to `Prepare` — never inside a Stateless entry/exit action — buys three things:

- Every await sits at an operation boundary. `CaseRuntime` is never torn across an await.
- Stateless transitions become **fully synchronous**. `PlanItemStateMachine.RetainSynchronizationContext = true` and the `FiringMode.Queued` reentrancy gymnastics (the 55-line comment at `PlanItemStateMachine.cs:36-89`, closing #64) exist *only* because `HandleEnterAvailableFromCreate` awaits an `IExpressionGrain` call from inside an entry action. That whole hazard class disappears.
- The operation budget in §B.4 has a clean unit of accounting: one operation, one bounded amount of work, one countable step.

### B.4 Termination

"It drains" is not a design — and neither is a termination proof this design cannot honestly supply. What follows is what *is* true, what is not, where the loop genuinely diverges, and what happens when it does.

#### B.4.1 The invariant that makes the argument possible

> **I-1 (progress).** An operation may enqueue successor operations **only if its `Apply` appended at least one journal event.**

This is enforceable: `Apply` receives the journal position before and after; the agenda rejects an enqueue from an operation that appended nothing. Under I-1, non-termination requires the journal to grow without bound *within a single turn*.

> **I-2 (purity).** Expression evaluation is side-effect free. Jint is sandboxed (`SandboxedJintEngine`), receives a bound `JsonNode` copy, and has no write path back into the case file.

I-2 is currently true and must stay true. If expressions ever gain the ability to mutate the case file, the argument below collapses entirely and this section must be rewritten before that ships.

#### B.4.2 What is monotone — and what that does not buy

An earlier draft of this section claimed a monotonicity *theorem*. **It does not hold, and this design no longer claims one.** The honest statement is narrower.

**What is true.** With the §C.9 evaluability gate in place, a *sentry* can cause a transition out of exactly two states:

| Criterion kind | Owner state while evaluable (§8.5, printed p.121) | Transition | From → To |
|---|---|---|---|
| Entry, Stage/Task | `Available` | `enable` / `start` | Available → Enabled / Active |
| Entry, Milestone | `Available` | `occur` | Available → Completed |
| Exit, Stage/Task | `Active` | `exit` | Active → Terminated |
| Exit, CasePlanModel | `Active` | `terminate` | Active → Terminated |

§8.5's opening paragraph is explicit that the exit case is Active-only: "When multiple exit criteria (sentries) are used only one is required to trigger to transition the `Stage`, `Task`, or `CasePlanModel` instance **from Active to Terminated**." So no sentry-caused transition ever moves an instance backwards, and no sentry ever fires `disable`, `re-enable`, `resume`, `parent resume`, or `re-activate` — Tables 8.6/8.8/8.11 assign all of those to a Case worker, an administrator, or parent propagation. (Table 8.8 `re-enable`: "Transition by a `Case` worker (human) decision." `resume`: "Transition by `Case` worker (human) decision or propagation from outer `Stage` instance." `re-activated`: "Transition by the systems, an administrator, or by `Case` worker (human)…".) That remains a useful sanity property and is worth asserting per-transition.

**What it does not buy.** It is not a termination argument, for four reasons:

1. **The rank order is partial.** `Suspended` and `Closed` have no place in any lifecycle rank. `Suspended` is entered and left by transitions the loop *does* run — R-C6's deferral drain fires on `resume` / `parent resume` — and Table 8.8's `exit` row admits `Suspended` as a source. The monotonicity assertion an implementer would have to write does not exist.
2. **Not every loop-driven transition comes from a sentry.** `create` (§C.6's `Uninitialized` arm and §C.11's bottom-up path), `manual start` (§C.11), `parent suspend` / `parent resume` (§C.4's cascades) and `fault` (§B.4.4's own divergence handler) are engine- or cascade-driven and sit outside the table above. Two of them decrease rank.
3. **The instance set is not bounded by the model.** Repetition creates instances at runtime — §B.4.3. A bound on ranks over a set that grows is not a bound.
4. **Every operation appends a journal event by design.** I-1 therefore constrains nothing about a diverging cascade: a cascade doing genuinely new work each step satisfies I-1 at every step.

**Stated plainly: the loop can diverge on a legitimate-but-pathological model.** It terminates on every model the corpus contains and on essentially anything a human would author, but there is no theorem and this document will not pretend to one. §B.4.3 gives the shapes; §B.4.4 specifies exactly what happens when it diverges. A stated bound with specified failure behaviour is defensible; a theorem that quietly depends on a spec deviation is not.

#### B.4.3 Where it actually diverges

The live instance count is **not** bounded by the model. Repetition creates instances at runtime. Four sub-cases:

- **Entry-criterion repetition** (§5.4.11.3, printed p.55; §8.6.4, printed p.123). A new instance per satisfaction of an entry criterion carrying an OnPart. Bounded by the number of source occurrences — *unless* the source is itself repeating, in which case B spawning B′ spawning B″ is exactly the spec's Example 1 (printed p.123) and is intended. Bounded in practice by whatever ends the chain.
- **No-entry-criteria repetition** (Table 8.8 `complete`/`terminate` rows, printed p.116). An instance that completes re-evaluates the RepetitionRule and creates a successor. If the successor is non-blocking, it completes inside the same turn and repeats. **This is a genuine unbounded loop** whenever the RepetitionRule is a constant `true`. It is not a hypothetical: `RepetitionGuardFootgunIntegrationTests` exists precisely because of it.
- **Mutual repetition across two items.** §8.6.4's own **Example 2** (printed pp.123-124) is two repeatable Tasks where each one's standard event satisfies the other's entry criterion. With both non-blocking, every completion spawns a successor on the other side *inside the same turn*, and the cascade is unbounded. This is not a contrived model — it is the spec's own illustration, and the finiteness in the spec's telling (three A, three B) comes from the modeller's intent, not from anything the engine can observe.
- **Timer repetition.** Not a source: each tick is a separate reminder turn (§C.7).

The middle two are why §B.4.4's backstops are **required**, not defensive.

#### B.4.4 The bound, the detector, and the fault

Three backstops. The first is model-aware; the other two are deliberately blunt.

**1. Per-plan-item repetition ceiling — retained, and it is the primary protection.** `RepetitionGuardOptions.MaxRepetitionsPerPlanItem` (#67) carries over unchanged, now enforced synchronously inside the loop rather than across a stream hop. It is the only backstop keyed to the thing that actually grows — instances of one plan item — so it catches both unbounded cases in §B.4.3 at a threshold an operator can reason about. On breach: journal `RepetitionCeilingExceeded`, fire `fault` on the containing CasePlanModel (today's behavior, pinned by `RepetitionGuardFootgunIntegrationTests`).

**2. Absolute operation budget.** `MaxOperationsPerTurn`. The backstop for a divergence the ceiling cannot see — an unbounded chain of *distinct* plan items, or a cascade that creates no instances at all. It must satisfy `budget × per-operation cost < ResponseTimeout` (§D.6.2). This is the only backstop that bounds **wall-clock** cost, which is what makes OQ-2 a sizing question with a consequence attached rather than a matter of taste.

**3. A repeat-signature detector, scoped.** Per turn, the agenda maintains a multiset keyed by `(operationKind, targetInstanceId, trigger)`; a signature recurring more than `MaxSignatureRepeats` times is reported as the culprit. Its value is diagnostic — it names something, which a bare counter cannot.

> **It must not be applied to `Instantiate`.** `Instantiate`'s key is ambiguous in exactly the way that decides whether the detector is useless or actively wrong. Keyed on the **new** instance, every signature is unique and it never fires. Keyed on the **containing stage**, legitimate repetition is indistinguishable from a cycle: §8.6.4 Example 1 produces three instances of B under one stage in one cascade — one signature, three times, clear of a threshold of 3 by exactly one, and any model that legitimately repeats four times in a turn trips it and faults a conforming case. **Design: the detector covers `Transition`, `ConsumeSatisfaction`, `EvaluateCompletion` and `PropagateContainment` only.** The repetition axis belongs to the #67 ceiling, which is model-aware; the budget is the catch-all.

On a trip of (1), (2) or (3), the loop stops and:

- journals `AgendaDivergenceDetected { reason, culpritSignature?, operationCount, lastNOperations }`,
- fires `fault` on the CasePlanModel (Table 8.8 `fault`: "Transition when an exception or software failure occurs. This state MUST NOT propagate." — printed p.115, and `ConfigureForCasePlanModel` already permits Active→Failed),
- confirms the journal,
- returns the snapshot (the caller sees a `Failed` case, not an exception).

**Why fault rather than abort.** Aborting the turn (throwing, never confirming) leaves no record of *what* diverged, which makes the bug undiagnosable. Faulting is the spec's own failure state, is auditable, and is recoverable by the spec's own `re-activate` — which Table 8.6 (printed p.113) grants to "a Case worker (human), or an administrator".

**The divergence failure mode is *worse* than today's, and that must be said.** Today a divergent model spreads across many turns: each spawn is a separate stream hop and a separate grain call, so the #67 ceiling trips early and cheaply, and no single request pays for it. In the redesign the entire cascade runs inside **one turn on the case grain's only thread** (§D.6.1). Three consequences, all new:

- The turn blows the 30 s response timeout (§D.6.2) and the caller gets a timeout instead of an answer.
- **The grain keeps running anyway** — a caller-side timeout does not cancel or roll back the turn (§D.6.2).
- When a backstop finally trips, the grain confirms a *single* journal write containing the whole divergent cascade, potentially a very large one, and every other request for that case has been queued behind it the entire time.

So the redesign trades many cheap, early, diagnosable failures for one expensive, late one. The mitigation is that `MaxOperationsPerTurn` must be derived from the response timeout rather than guessed, and that the #67 ceiling should stay tight enough to trip first in the common case. This is a genuine regression in operational behaviour and is accepted knowingly, not overlooked.

> **Open question OQ-1.** The cost of faulting is that a divergent cascade's partial effects become durable. An operator who re-activates a case that diverged resumes from a state the model never intended. The alternative — abort without confirming, and journal the divergence into a *separate* diagnostics sink — keeps the case clean but needs a second write target. Recommendation: fault. Reviewers should push on whether "durable partial cascade" is acceptable.

**OQ-2 (was an open question; now a measurement task).** `MaxOperationsPerTurn` and `MaxSignatureRepeats` are not open *questions* — they are unmeasured constants, and the harness to measure them already exists. `src/Wayfinder.Benchmarks/Expressions/` contains `ExpressionEvaluationBenchmarks.cs` (production-shape evaluation, warm-engine and warm-executable variants, `[MemoryDiagnoser]`) and `JintEngineBenchmarks.cs` (engine construction alone, #224). Per-operation cost is dominated by expression evaluation, which is exactly what those two measure. **Action, not a decision:** run them, divide the response timeout by the measured worst-case per-operation cost, and write the arithmetic down next to the constant. Recorded in [§H.3](#h3-closed--these-were-never-questions) as closed rather than open.

### B.5 Journal semantics

#### B.5.1 Commands vs. transitions — **both**, at different levels

| Event class | Purpose | Replayed? |
|---|---|---|
| `CommandReceived { commandId, actor, tenant, method, arguments, correlationId }` | Audit ("who asked for what") and idempotency (§D.6.3) | **Yes — `commandId` only** (see below) |
| Effect events — `Transitioned`, `PlanItemInstantiated`, `SentryOccurrenceRecorded`, `SentrySatisfied`, `RuleEvaluated`, `CaseFileItemValueChanged`, `TimerArmed`, `TimerFired`, `EffectDeferred`, … | Reconstruct state | **Yes** |
| `TurnCompleted { commandId, operationCount, journalPositionRange }` | Marks the fixpoint boundary; makes "what did this one command cause" answerable in the audit trail | **No** (fold ignores it) |

**`CommandReceived` must be folded, or idempotency does not survive activation.** An earlier draft marked it "Replayed? No". That is inconsistent with §D.6.3, which keeps a bounded ring of recent command ids **in state**: a ring built from an event the fold ignores is empty after every activation, so the first retry following a deactivation double-applies — precisely the case idempotency exists for. The fold therefore **does** process `CommandReceived`, but its only effect is `ring.Push(commandId)`. The actor, tenant, method, arguments and correlation id are audit payload and are never re-read by the fold. That is the narrowest thing that works, and it is stated here because "replayed / not replayed" is too coarse a column for this event.

**Effect events are authoritative for replay; commands are never re-executed.** This is not a stylistic choice. `JournaledGrain` replay is a pure fold — entry actions do not re-run. Re-deriving state by replaying *commands* would require re-running the agenda loop, which is only sound if the loop is deterministic, and it is not: expression evaluation goes through Jint, and timer arithmetic reads the clock. Recording commands only would reproduce the D1 defect in a new form.

#### B.5.2 What a replay must reconstruct

Exhaustively, because anything omitted here is a silent post-restart bug:

1. Every plan-item instance: id, repetition ordinal, `PlanItem`/`PlanItemDefinition` binding, parent link, creation order.
2. Every instance's `State` **and** `ParentSuspendState` (Figure 8.3's history pseudo-state, printed p.113; Table 8.9 Note (2), printed p.119). *This is where M3 lives today* — `PlanItemStore.Apply(ParentSuspended)` records the state at apply time rather than the transition's source. In the redesign, `Transitioned` carries `Source`, `Destination`, and `Trigger`, and `ParentSuspendState` is derived from `Source` during the fold. M3 is closed by construction.
3. Every memoized rule value: `Required`, `ManualActivation`, `Repeatable`, plus each rule's last evaluation error.
4. Every sentry instance's `Pending` map (per-OnPart occurrence with its event ordinal) and its `SatisfactionCount` (§C.8 — a counter, **not** a ledger).
5. Every deferred effect held under suspension (§C.6).
6. Every case-file item's value, lifecycle state, children/targetRefs, and per-item logical version.
7. Every armed timer: anchor instant, parsed schedule, remaining occurrence count, next fire instant, and the reminder name it maps to.
8. The `UserCompletable` latch per stage.
9. `NextInstanceOrdinal` and `NextEventOrdinal`.
10. The bounded ring of recent `CommandId`s (§D.6.3), folded from `CommandReceived`.

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

**Decision D-C2-1 (provisional — see OQ-3, which is blocking).** The stage **completes**, and the Table 8.9 cascade (already implemented for #179 at `StageBehavior.cs:525-533`) drives the Suspended non-required child to Terminated via `exit`. A **required** Suspended child still blocks, because Suspended ∉ {Disabled, Completed, Terminated, Failed}.

**The justification has been corrected.** An earlier draft argued "Table 8.12 *permits* the completion; Table 8.9 then *requires* the cascade". The second half is a misreading and is withdrawn. Table 8.9's `complete` rows (printed p.119) give, for a Stage/Task child in Available, Enabled, Active or Suspended under a completing parent, **Transition = `N/A`, To state = `<impossible>`**. `N/A` is the *absence* of a prescribed cascade, not a mandate to fire one. The only rows that prescribe `exit` are the **`exit, terminate`** rows, which apply when the parent transitions to Terminated — a different parent transition entirely.

So the honest rationale is narrower: Table 8.12's TRUE column is satisfied (Suspended is not Active; the child is not required), Table 8.9 declines to say what happens to the child, and firing `exit` is the engine's own choice of how to restore I-3 (§C.3) — the same choice #179 already made for Available/Enabled children, and the same one the manual-completion path already makes. It is a defensible deviation. It is not something the spec requires.

> **Open question OQ-3 — blocking before P2.** The opposite reading, that `<impossible>` means "do not complete", is **better supported than the earlier draft allowed**, and this changes the balance rather than merely noting a dissent.
>
> The earlier draft's counter-argument was that the dissent "would require a fifth conjunct Table 8.12 does not state". Table 8.12 does not state it — but **Table 8.7 does, in the state semantics**. Table 8.7's `Active` description (printed p.114) reads: "The `Stage` or `Task` considered instance is executing in this state. `Stage` instances in this state contain at least one `Stage` or `Task` instance in the Available, Enabled, Active, **Suspended** state, or autoComplete is set to FALSE." A Stage holding a Suspended child is, by that description, still Active — which is exactly the conjunct the dissent needs, sourced from the spec rather than invented. (The `or autoComplete is set to FALSE` disjunct means the evidence bites hardest in the `autoComplete = TRUE` case, which is the case D-C2-1 is about.)
>
> Against that: Table 8.7 is a *state description*, not a normative completion criterion, and Table 8.12 is the table §8.6.1 designates for termination criteria. Reading a hard conjunct out of a descriptive sentence is a real interpretive move.
>
> This is now genuinely two-sided and it is **blocking**: it changes `EvaluateCompletion`'s predicate and the expected final states of any corpus scenario that suspends a non-required child, both of which land in P2. The one thing that remains unacceptable either way is today's behavior, where the answer depends on the order two events arrived in.

**The root is not a Stage for this purpose.** `EvaluateCompletion(stage)` must **not** apply Table 8.12 uniformly to the CasePlanModel. Table 8.6's `complete` row (printed p.112) states the Case-level criterion separately and differently: "Transition when all the required `Milestone`, `Stage`, and `Task` instances have reached a terminal state (Closed and Terminated) or a semi-terminal state (Completed, Disabled, and Failed), and there are no executing (Active) `Stage` or `Task` instances." Three differences from Table 8.12: the acceptable set includes **`Closed`**; there is **no `autoComplete` branch** — the criterion is unconditional, and corresponds to Table 8.12's TRUE column; and there is **no DiscretionaryItems clause**. `EvaluateCompletion` therefore branches on whether the target is the root, and the root's predicate is Table 8.6's. Small, but it is the difference between a case that completes and one that does not for any model whose casePlanModel carries `autoComplete="false"`.

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
| casePlanModel (Table 8.6) | `suspend`, `terminate`, `re-activate` ("by a Case worker (human), or an administrator"), `close` ("by the system, an administrator, or Case worker (human)") | `create`, `fault`, **`complete`** |
| Stage (Table 8.8) | `disable`, `manual start`, `suspend`, `resume`, `re-enable`, `re-activated`, `terminate` | `create`, `enable`, `start`, `exit`, `fault`, `parent suspend`, `parent resume`, **`complete`** |
| Task, non-human (Table 8.8) | `disable`, `manual start`, `suspend`, `resume`, `re-enable`, `re-activated`, `terminate` | `create`, `enable`, `start`, `exit`, `fault`, `parent suspend`, `parent resume`, **`complete`** |
| **HumanTask** (Table 8.8) | `disable`, `manual start`, `suspend`, `resume`, `re-enable`, `re-activated`, `terminate`, **`complete`** | `create`, `enable`, `start`, `exit`, `fault`, `parent suspend`, `parent resume` |
| EventListener / Milestone (Table 8.11) | `suspend`, `resume`, `terminate`; **`occur` only for a `UserEventListener`** | `create`, `occur` (Milestone: "when one of the achieving `Sentries` is satisfied"; TimerEventListener: a tick), `parent terminate` |

Table 8.11's `occur` row is explicit about the split: "For event listener transitions when the event being listened by the `EventListener` instance does occur. For a `UserEventListener` instance this transition happens when a `Case` worker (human) decides to raise the event. For `Milestone` instance transitions when one of the achieving `Sentries` (entry critera) is satisfied."

**`complete` is *not* a case-worker transition in general, and an earlier draft got this wrong.** Every other entry in the case-worker column above is backed by an explicit "Transition by `Case` worker (human)…" sentence in the Description column. Neither `complete` row carries one:

- Table 8.6 `complete` (printed p.112): "Transition when all the required `Milestone`, `Stage`, and `Task` instances have reached a terminal state … and there are no executing (Active) `Stage` or `Task` instances." A *condition*, no actor.
- Table 8.8 `complete` (printed p.116): "Transition when the `Stage` or `Task` instance completes normally. For a `Stage` instance, the termination criteria described in Table 8.12 … must be satisfied. For a `Task` instance, this means its purpose has been accomplished (`CaseTask` instances have launched a new `Case` instance; … **`HumanTask` instances have been completed by a human**; etc.)."

The only actor the spec names anywhere near `complete` is the human completing a **HumanTask**. Admitting `complete` for a Stage would let a caller complete a Stage with Active children and bypass Table 8.12 outright — the same defect shape as the `Trigger(Fault)` fabrication this section exists to close, so it would have been a new hole opened by the fix for an old one.

**Consequence for the allowlist key.** `(elementKind, transition)` is the right key only if `elementKind` distinguishes **HumanTask from generic Task and from Stage** — a distinction the current `PlanItemTransition` surface does not make at all. For the **casePlanModel** there is no Manual Completion branch to lean on either (Table 8.6 has no `autoComplete` and no manual-completion clause — §C.2), so `complete` on the root is engine-only, full stop. A case worker who wants a Stage to finish uses Table 8.12's Manual Completion path (the `UserCompletable` latch, §A.2), which is a different operation with its own preconditions — not `Trigger(complete)`.

**Design.**

1. A single static allowlist table keyed by `(elementKind, transition)`, consulted in the command preamble (step 2 of §B.1), **before** anything touches the agenda.
2. Rejection is an explicit, distinguishable error — **not** the current silent no-op via `HandleUnhandledTrigger`. Three outcomes must be distinguishable at the API:
   - transition not permitted for a case worker on this element kind → `ResultStatus.BadRequest`
   - transition permitted but not fireable from the current state → `ResultStatus.Conflict`
   - caller lacks the required role (`UserEventListener.authorizedRoleRefs`) → `ResultStatus.Unauthorized`
3. **Narrow the wire enum.** `Wayfinder.Contracts.V1.PlanItemTransition` currently exposes all 18 values including `ParentSuspend`, `ParentResume`, `Create`, `Start`, `Enable`, `Exit`, `Fault`, `Complete` and `ParentTerminate`. Replace it with a `CaseWorkerTransition` enum containing only the allowlisted set — which, per the table above, is **element-kind-dependent**, so the enum is the union and the `(elementKind, transition)` table is still consulted.
   - This also removes `ParentTerminate` from the wire, which is finding **I3**. Stated precisely: `parent terminate` **is** a real spec transition — Table 8.11 (printed p.121) defines it, "Available, Suspend → Terminated: Transition when the parent stage transition to terminate." What it is missing from is the **interchange enumeration**: Table 5.31 (printed pp.35-36) lists exactly 17 `PlanItemTransition` values and `parentTerminate` is not among them, yet `Spec.CMMN.MODEL.cs:2760-2763` hand-appends it to the XSD-generated enum. So it does not belong on the *wire* (a `standardEvent` an OnPart could name), and it does belong in the *engine* (a transition the cascade fires). Calling it "the non-spec `ParentTerminate`" overstated I3 and is corrected here. The interchange half of I3 remains orthogonal and out of scope.
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

New rule. The only state predicate that survives is **"is the owner still in `Available`?"** — everything else is one arm:

```
ConsumeSatisfaction(criterion, satisfaction):
    owner = criterion.Owner

    if owner or any ancestor is Suspended:
        defer(owner, CriterionSatisfaction)                       // R-C6
        return

    if criterion.Kind == Exit:
        // §8.5 / §C.9 make Active the only evaluable state, so there is no ladder here
        fire exit (Stage/Task) | terminate (CasePlanModel), carrying criterion.Id
        return

    // entry criterion
    switch (owner.Kind, owner.State):
      (_,             Uninitialized) -> fire create, then re-run this operation   // §C.11
      (Stage|Task,    Available)     -> ManualActivationRule ? enable : start     // T8.8, printed p.115
      (Milestone,     Available)     -> fire occur                                // T8.11, printed p.121
      (_,             anything else) -> RepeatFrom(owner, satisfaction)

RepeatFrom(owner, satisfaction):
    if not satisfaction.OnPartOccurred:
        journal SatisfactionIgnoredNoOnPart; return               // §5.4.11.3 requires an OnPart
    Prepare: repeat = evaluate(owner.Item.EffectiveControl.RepetitionRule)   // re-evaluated EVERY time
    if not repeat:
        journal RepetitionRuleDeclined; return
    if owner.Parent.State is terminal or Disabled:
        journal RepetitionRefusedContainerNotLive; return         // see (c) below
    agenda.Push(Instantiate(owner.Parent, owner.Item, owner.Repetition + 1,
                            forwardedSatisfaction: satisfaction))
```

Four things in that rewrite are corrections to an earlier draft, and each one would have produced a wrong engine.

**(a) The new instance must be *started*, not left in `Available`.** This is the important one. §8.6.4 (printed p.**123**) does not stop at creation:

> "…the `RepetitionRule` is re-evaluated and if the `Expression` evaluated to TRUE, then the new instance is created and **because the entry criteria is satisfied it moves from the Available state to either Active or Enabled state depending on the `ManualActivationRule`**."

Under D-C8-1 every instance gets its own private sentry copy with **empty occurrence memory**. That is the right topology, but it means the new instance's entry criterion is unsatisfied *by construction*, and nothing that already happened can ever satisfy it — the occurrence that caused the creation was consumed on the predecessor's sentry copy. An earlier draft claimed the empty memory *solved* #177; left alone it **re-creates** #177 structurally, with every repeating plan item stalling in `Available` forever.

The fix is the `forwardedSatisfaction` parameter on operation 4 (§B.3): `Instantiate` creates the instance, arms its criteria, fires `create`, and then enqueues `ConsumeSatisfaction` against the **new** instance's criterion carrying the *same* satisfaction. §C.11's bottom-up path already does exactly this (`ConsumeSatisfaction(targetInstance, satisfaction)` once the instance is located); the repetition path needs the identical step, and now has it. Note the forwarded consumption re-enters the switch above at the `Available` arm, so `enable`-vs-`start` is decided by the ManualActivationRule for the *new* instance, which is what the quoted sentence requires.

**(b) There is no `Disabled` or terminal refusal on the owner.** An earlier draft refused to spawn a repetition when the owner was `Disabled` or terminal, asserting that §8.6.4 grants the trigger only to instances that have left `Available` and "explicitly excludes `Disabled`". **Both assertions are unsupported.** §5.4.11.3 (printed p.55) is unqualified:

> "After that, **every time** an entry criterion with an `OnPart` is satisfied the `RepetitionRule`'s condition is re-evaluated and if it evaluates to TRUE, a new instance of the `Task`, `Stage`, or `Milestone` is created and transition to Available."

No state qualifier appears there, in §8.6.4's parallel sentence, or anywhere in Table 8.8. The spec is **silent** on `Disabled` and on terminal owners — not permissive, not prohibitive, silent — and "explicitly excludes" was a claim the text does not support. (§8.6.4's Example 2 is sometimes read as settling the terminal case; it does not. It says a second satisfaction of A's entry criterion creates a new A, without ever stating what state the previous A was in. It establishes the *count* — three A instances — not the *guard*.)

Independently of the spec, the refusal was a design error: it made the outcome depend on whether the predecessor happened to still be `Active` when the satisfaction arrived, which is precisely the transition-order dependence §C.2 exists to eliminate. Collapsing `Enabled`, `Active`, `Disabled`, `Failed`, `Completed` and `Terminated` into one arm removes that dependence.

**(c) The guard that *is* kept is on the container, not the owner.** A repetition is created *in* a stage; if that stage is terminal or Disabled there is nowhere live to put it. This is the axis the existing `RepetitionAfterTerminationIntegrationTests` (#178) already pins — `StageBehavior.cs:1161` switches on `Host.State.PlanItemState`, the **containing stage's** state, and the test asserts on the stage's snapshot, never on the child's. Keeping the container guard and dropping the owner guard therefore *preserves* #178's rule rather than silently replacing it with a different one, which is what the earlier draft did.

**(d) A Milestone does not `enable` or `start`.** The earlier draft's `Available -> ManualActivationRule ? enable : start` was written as the general rule and is invalid for a Milestone: Table 5.51 (printed p.52) says a `PlanItemControl` that is the `defaultControl` of an EventListener or Milestone "MUST NOT contain a `ManualActivationRule`"; Table 5.55 (printed p.55) marks ManualActivationRule **N/A** for Milestone; and Table 8.11 (printed pp.120-121) gives a Milestone no `enable` and no `start` at all — from `Available` it has only `occur`, `suspend`, `terminate` and `parent terminate`. The engine already gets this right (`PlanItemStateMachine.ConfigureForMilestoneOrEventListener`, `PlanItemStateMachine.cs:288-292`, permits exactly those four); the document was specifying a regression. Note also that Milestones **do** take a RepetitionRule (Table 5.55: Applicable; §5.4.11.3 names the Milestone explicitly), so the `RepeatFrom` arm is reachable for them and must be.

> **Incidental finding, flagged not fixed.** Table 5.51 (printed p.52) states: "If no `ManualActivationRule` is specified, then the default is considered **TRUE**." Combined with Table 8.8's `start` row ("This transition only happens if the `ManualActivationRule` evaluates to FALSE"), an item with no ManualActivationRule should `enable` into `Enabled` and wait for a human — not `start`. That is the opposite of the usual implementation instinct and is **not** what §C.1's RequiredRule default (FALSE, §8.6.3) does. Whether the current engine agrees was not checked and is out of scope for this document; it deserves its own issue against the corpus before P2.

The satisfaction itself is still **recorded** on the sentry regardless of any of the above (§8.5 defines OnPart satisfaction as a past event; nothing un-satisfies it). Only the *consumption* defers.

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

Table 5.55 (printed p.55) gives EventListener `N/A` for RepetitionRule. §5.4.11.3 (printed p.55): "`EventListeners` cannot have `RepetitionRule`. … However, for a `TimerEventListener` repetition can be defined via a `timerExpression` based on ISO-8601, by defining repeating intervals in it (using `"R<n>/"` notation)."

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

**Spec, verbatim.** §8.5 (printed p.121), **from its first sentence** — an earlier draft began the quote a paragraph too late and lost the OR-across-criteria rule entirely:

> "When multiple entry criteria (sentries) are used only one is required to trigger the transition of the `Stage`, `Task`, or `Milestone` instance out of Available state. The same is TRUE for exit criteria. When multiple exit criteria (sentries) are used only one is required to trigger to transition the `Stage`, `Task`, or `CasePlanModel` instance from Active to Terminated.
>
> A `Sentry`'s `OnPart` is satisfied when one of the following conditions is satisfied:
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

**R-C8-0 (OR across criteria).** That first paragraph is normative and nothing else in this design states it: **criteria are OR-ed, sentries are AND-joins.** An instance with three entry criteria leaves `Available` on the *first* one satisfied; the other two are not consulted, not waited for, and not required ever to be satisfied. Likewise an instance with three exit criteria terminates on the first. Concretely, in the algebra below: `ConsumeSatisfaction` fires the owner's transition on whichever criterion satisfies first, and the losing criteria simply keep their recorded occurrences — they are not cancelled, because a *repetition* spawned later may still be triggered by one of them (§C.6). The AND-ing that §8.5's bullet list describes is strictly *within* one sentry, across its OnParts.

Two consequences follow immediately, and both are easy to get wrong: the two paragraphs together mean the exit path is **`Active` → `Terminated` only** for criterion-driven exits (§C.9, §B.4.2), and the "only one is required" rule means an implementation must **not** clear or rearm the sibling criteria when one wins.

#### Decision D-C8-1 — one sentry instance per *criterion instance*

Today one `SentryGrain` exists per `(owning stage instance, sentry definition)`, shared by every criterion referencing it, and minted with a fresh `ShortGuid` on **every stage activation** — which is finding C5 (each idle reactivation adds a duplicate live sentry set).

**Design: a sentry instance is owned by the criterion instance that references it.** Each plan-item instance gets its own private copy of every sentry its entry/exit criteria name. Consequences:

- **C5 dies twice over**: sentry instances are created exactly once, inside the `Instantiate` operation, recorded in the journal, and reconstructed by the fold. There is no per-activation minting.
- **#177 gets much simpler — but fresh state alone does not close it.** A repetition instance needs fresh entry-criterion state, and with per-criterion sentries it gets one automatically, with empty occurrence memory. Today's shared, latched sentry is the whole reason a spawned instance "can miss the very satisfaction that created it". **The empty memory is necessary and not sufficient:** it also guarantees the new instance's entry criterion is unsatisfiable from anything that already happened, so the satisfaction that caused the creation must be *forwarded* to the new instance explicitly. That is `Instantiate`'s `forwardedSatisfaction` parameter (§B.3 operation 4, §C.6(a)). Without it this decision re-creates #177 rather than closing it.
- **Evaluability (§8.5's "ready for evaluation while…") becomes local.** A sentry copy knows exactly one owner and one criterion kind, so the gate is a single field read. With a shared sentry the gate is per-referrer and the sentry could be "satisfied for one referrer and not another" — a state with no clean representation.
- **Cost:** an IfPart shared by *k* criteria is evaluated *k* times per relevant event. Bounded, in-memory, and mitigable by memoizing `(expression body, contextRef, case-file version)` within a turn — sound under invariant I-2 (§B.4.1).

> **Open question OQ-7.** The alternative (one sentry instance per owning-stage-instance, shared) is closer to today's topology and cheaper. It requires satisfaction to fan out to referrers and consumption to be tracked per referrer, which reintroduces the "satisfied for whom?" problem. This design takes per-criterion. Reviewers should weigh the duplicate-evaluation cost against the #177 simplification.

#### The algebra

For a sentry instance `S` with OnParts `O₁..Oₙ` (n ≥ 0) and optional IfPart `φ`:

```
State:  Pending[i]         : Occurrence?      // most recent UNCONSUMED occurrence of Oᵢ
        SatisfactionCount  : long             // discrete satisfactions; NOT a monotonic bool
```

**Why a counter and not a ledger.** An earlier draft held `List<SatisfactionRecord> Satisfactions`. That list is **unbounded** — six of the eight `CaseFileItemTransition` values are repeatable `Available → Available` (Table 5.29, printed p.34), which this section itself calls the common case, so a long-running case accumulates satisfaction records without limit, replay must reconstruct every one of them, and they sit in the activation working set for the life of the case (§D.5.4). And **nothing reads a historical satisfaction.** The only consumers are: "has this sentry ever satisfied" (the standalone-IfPart case), "how many times" (diagnostics and the `SentrySnapshot` in §D.4.2), and "which satisfactions are still deferred under suspension" — and the third is already held, correctly and boundedly, in the owner's `PendingWhileSuspended` list (§C.6). A counter plus that list is exactly the retained information with none of the growth. The journal still carries every `SentrySatisfied` event, so the full history remains available through `GetJournaledEvents` for audit; it simply is not folded into resident state.

**An occurrence is identified by the case-monotonic event ordinal of the transition that produced it.** That ordinal is the journal position, assigned by the single writer. This is the direct replacement for `SentryStore`'s `OccurrenceToken` and is where **M8** dies: two distinct occurrences by the same source instance necessarily carry different ordinals, and a "redelivery" cannot exist at all because there is no transport. `SentryStore.IsRedelivery` is deleted.

**Evaluation** — operation 2, `RecordAndEvaluateSentry`:

```
1. Pending[i] := occurrence                      // record UNCONDITIONALLY; last-wins per OnPart
2. if not Evaluable(S):  stop                    // §C.9's gate — the occurrence stays Pending
3. if n > 0 and any Pending[j] is null:  stop    // AND-join incomplete
4. if φ exists:
     evaluate φ against the CURRENT case file
     faulted -> journal SentryIfPartFaulted; CONSUME NOTHING; stop
     false   -> journal SentryNotSatisfied{IfPartFalse}; CONSUME NOTHING; stop
     true    -> satisfied
   else: satisfied
5. satisfied:
     journal SentrySatisfied { sentryInstanceId, consumedOrdinals[], satisfactionOrdinal, onPartOccurred }
     SatisfactionCount++
     Pending[*] := null                          // consume
     agenda.Push(ConsumeSatisfaction(owningCriterion, thisSatisfaction))

Evaluable(S) :=                                  // §8.5, printed p.121 — normative, see §C.9
    S.Kind == Entry ? S.Owner.State in { Available, Uninitialized }
                    : S.Owner.State == Active
```

**Step 2 is not optional, and an earlier draft omitted it.** §C.9 asserted the "ready for evaluation while…" gate but the four steps here never checked the owner's state, so the gate had no point of entry and would not have been implemented. §8.5's sentence is normative, and without step 2 there is a concrete break: a Task carrying **both** an entry criterion and an exit criterion whose OnParts match the same CaseFileItem `update`. The exit sentry has no IfPart, so it falls straight through to "satisfied" while the task is still `Available` — terminating the task before it ever starts, and doing so order-dependently, since whether the entry sentry gets there first is an accident of evaluation order. With step 2 the exit sentry records the occurrence and stops.

**Steps 1 and 2–5 are one atomic operation and must never be split.** If recording and evaluating were separate agenda entries, two occurrences of the same OnPart arriving in one turn would both be recorded before either is evaluated; the second overwrite would erase the first and one satisfaction would be silently lost. Recording-and-evaluating together makes that impossible.

**R-C8-1 (re-evaluate on becoming evaluable).** Step 2 stopping is not the end of the story, and this is the half that is easy to forget: **a sentry whose AND-join completed while its owner was not yet evaluable must be re-evaluated when the owner *becomes* evaluable.** Otherwise an exit sentry that fully joined while the task sat in `Available` never fires once the task reaches `Active`, and the occurrence sits Pending forever. Therefore: any `Transition` whose `Apply` moves an instance **into** `Available` or **into** `Active` MUST enqueue `RecordAndEvaluateSentry` (with no new occurrence) for every one of that instance's sentries whose kind is now evaluable. This is the transition-driven twin of M10's case-file-driven re-evaluation below, and it is cheap for the same reason: step 3 short-circuits on a field read whenever the join is incomplete.

**What each finding maps to:**

- **C4** — "a satisfied sentry re-fires even when its IfPart evaluates FALSE." Today `SentryGrain.cs:367` gates the publish on `if (!State.Satisfied) return;` where `SentryStore.Satisfied` is a monotonic latch never cleared (`SentryStore.cs:75-79`). The IfPart-FALSE branch raises `OnPartNotRearmed` and then **falls through to the latched publish**. In the new algebra there is no latch to consult: step 4 is the only publish point and is reachable only from step 3's `true` branch. `SentryStore.Satisfied` as a `bool` is **deleted**; satisfaction becomes a ledger of discrete events.
- **M9** — "CaseFileItemOnPart without IfPart latches after the first occurrence." Today `SentryGrain.cs:203` passes `occurrenceToken: null`, and `IsRedelivery` treats any null-token occurrence as a redelivery once one is recorded. Gone: every occurrence carries an event ordinal, so a second CFI update produces a fresh `Pending[i]`, step 2 passes, no IfPart, satisfied again. Table 5.29 (printed p.34) makes six of the eight `CaseFileItemTransition` values repeatable Available→Available transitions (`update`, `replace`, `addChild`, `removeChild`, `addReference`, `removeReference`), so this is the common case, not an edge.
- **The C4/M9 cancellation** — both defects vanish in the same change, which is the only safe way to remove either.
- **M10** — "IfPart never re-evaluated on CaseFileItem changes; `OnPartNotRearmed` can make a sentry permanently unsatisfiable." Two halves, two fixes:
  1. **Do not consume on FALSE.** Today `Apply(OnPartNotRearmed)` *deletes* the triggering occurrence (`SentryStore.cs:99-103`). §8.5 defines OnPart satisfaction as a past event — "its `sourceRef` transitions into the transition described by the `standardEvent`" — and nothing in §8.5 un-satisfies it. The occurrence therefore **stays Pending**, and a later re-evaluation can satisfy the sentry. `OnPartNotRearmed` is deleted.
  2. **Re-evaluate on case-file events.** Every case-file item transition enqueues `RecordAndEvaluateSentry` (with no new occurrence) for **every sentry instance that has an IfPart and satisfies `Evaluable(S)`** — the same predicate as step 2, per §8.5's "ready for evaluation while…" sentence. Step 3 short-circuits cheaply when the AND-join is incomplete, so the cost is O(sentries) field reads plus one expression evaluation per sentry whose join is already complete. Together with **R-C8-1** above, a sentry is re-examined on exactly two triggers: the case file changed under it, or its owner became evaluable.
  This makes the review's canonical M10 scenario work: B completes while `amount == 50` → occurrence stays Pending, IfPart false; later `amount = 200` → the case-file event re-evaluates → satisfied.
- **`Pending` is last-wins, not a queue.** §8.5's AND-join has no notion of pairing occurrences across OnParts, and a queue would invent an arbitrary pairing rule. Last-wins loses nothing observable: with the atomic record-and-evaluate above, an earlier occurrence is only overwritten while a *sibling* OnPart's join is still incomplete, where the older occurrence is exactly the one that should be forgotten.
- **`sentryRef` is not a live mechanism — it is CMMN 1.0 residue, and there is only one thing to implement.** §8.5's first bullet reads "For a `PlanItemOnPart`, its `Sentry` referred by `sentryRef` has occurred", and Table 5.30's `exitCriterionRef` description ends "When `sentryRef` is specified, `standardEvent` MUST have value 'exit.'" Both sentences survive in the prose. **`sentryRef` is not among `PlanItemOnPart`'s attributes in CMMN 1.1**: Table 5.30 (printed p.35) lists exactly three — `standardEvent : PlanItemTransition`, `sourceRef : PlanItem[0..1]`, and `exitCriterionRef : ExitCriterion[0..1]`. A model literally cannot carry a `sentryRef`, so there is nothing to match against it. The 1.1 replacement is `exitCriterionRef`, whose own description states the semantics the `sentryRef` bullet was describing: "It enforces that the `PlanItemOnPart` of the `Sentry` occurs when the `PlanItem` that is referenced by `sourceRef` transits by the specified `exitCriterion` due to the `Sentry` that is refers being satisfied." An earlier draft read the two as two live mechanisms; they are one, and the design implements `exitCriterionRef` only. Table 5.30's other constraint — that the referenced ExitCriterion be contained by the PlanItem named by `sourceRef` — is lintable, and the runtime match is a direct reference, not a string search. This is preserved end-to-end (§G.1), but the Stateless parameterized-`Exit` trick that currently threads `exitCriterionRef` through `Transition.Parameters` (`PlanItemStateMachine.cs:25,98-107`; `BaseBehavior.cs:166-168`) becomes unnecessary — the agenda operation carries it directly.
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
- **Exit criterion**: evaluable while the owner is `Active` (§8.5: "while the `CasePlanModel`, `State`, or `Task` is in Active state" — "State" is a typo for "Stage" in the published spec; §8.5's opening paragraph says the same thing unambiguously: "from Active to Terminated").
- **Suspended owner**: the occurrence is recorded, and consumption defers (§C.6).

Occurrences are **always recorded**, regardless of evaluability — that is #186's discipline ("journal the satisfaction unconditionally… the journaled FACT that the criterion was satisfied is not conditional") generalised.

**Where the gate actually enters the code, which an earlier draft did not say.** Asserting a gate in prose and never giving it an insertion point is how it fails to get built. It enters in exactly two places, both specified in §C.8:

1. `Evaluable(S)` as **step 2** of `RecordAndEvaluateSentry` — between recording the occurrence and testing the AND-join. Recording is never gated; evaluation always is.
2. **R-C8-1**, the re-evaluation enqueued by any transition that moves an instance *into* `Available` or *into* `Active`. Without this the gate is a one-way trapdoor: a sentry that fully joined while its owner was ineligible would never be looked at again.

#### C.9a The gate does **not** narrow Table 8.8's `exit` row — reconciling the two readings

This needs saying explicitly, because the two statements look contradictory and picking one silently would regress the best-verified asset in the codebase.

Table 8.8's `exit` row (printed p.116) gives **From = {Available, Active, Enabled, Disabled, Suspended, Failed} → Terminated**, and Table 5.31 (printed p.36) repeats the same six. `PlanItemStateMachine.ConfigureForStageOrTask` implements all six today (`PlanItemStateMachine.cs:221, 232, 241, 254, 264, 276`), and [04 §10](04-adversarial-review-2026-08.md) lists the near-cell-perfect lifecycle tables as the top preservation item. §8.5 says exit criterion sentries are evaluable only while the owner is `Active`. Taken carelessly, the gate makes five of those six source states unreachable.

**They are not in conflict, because `exit` has two callers.** Table 8.8's own `exit` description names both: "Transition when the exit criteria of the `Stage` or `Task` instance becomes TRUE, **or when the parent `Stage` instance transitions to Terminate state**."

| Caller | Permitted source states | Governed by |
|---|---|---|
| An **exit criterion** on the instance itself | `Active` only | §8.5 (both the opening paragraph and the "ready for evaluation" sentence) |
| The **parent-terminate cascade** | all six | Table 8.9's `exit, terminate` rows (printed p.119), which prescribe `exit` from Available, Enabled, Disabled, Active, Suspended and Failed |

So the six-state `From` set exists **because** `exit` is the vehicle for Table 8.9's termination cascade, not because a sentry can fire it from six states. The state machine keeps all six permits, unchanged, and `docs/04`'s preservation item is honoured in full. What the gate constrains is which *operation* may fire it: `ConsumeSatisfaction` (§C.6) fires `exit` only from `Active`; the cascade table (§C.4) fires it from any of the six. §B.4.2's table of sentry-caused transitions is stated on the sentry-driven path only, for the same reason.

The CasePlanModel is a further special case in the same direction and is already implemented correctly: `ConfigureForCasePlanModel` deliberately has **no** `Exit` permit at all (`PlanItemStateMachine.cs:144-157`, ADO #66) because Table 8.6 gives the root `terminate`, not `exit`; `CasePlanModelBehavior` overrides `ExitCriterionTransition` to fire `Terminate`. Preserved verbatim.

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
    // Table 8.7, printed p.114: "the Stage instance and recursively all Stage instances
    // UP TO THE ENCLOSING STAGE OF THE STAGE OR TASK IN WHICH THE STAGE OR TASK RESIDES
    // moves into Active state if not already active."
    // The chain is defined on the TARGET's ancestry. The source is not mentioned at all.
    chain = [ancestor Stages of targetPlanItemModel, from the CasePlanModel inward,
             ending at and including the target's own enclosing Stage]

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

**The stopping rule is on the target's ancestry, not the source's — an earlier draft had this wrong.** It said the chain stops "at the enclosing Stage of the source item". Table 8.7 says nothing about the source; it terminates the recursion at "the enclosing `Stage` of the `Stage` or `Task` **in which the `Stage` or `Task` resides**", i.e. the target's own parent stage. The difference is not cosmetic: for a source in a *sibling* subtree, the source's enclosing stage is not on the target's ancestor chain at all, so the earlier terminator was not merely wrong but undefined — the walk had no stopping condition to hit. The correct chain is purely a property of the target, and since ancestors already `Active` are skipped (the `Active -> continue` arm), the walk does real work only from the outermost not-yet-Active ancestor inward. The root CasePlanModel is `Active` for the whole life of a running case (Table 8.6 `create`), so the walk always terminates.

Because activation runs **outermost-first**, the innermost stage's `HandleEnterActiveFromStart` creates the target as an ordinary child. The bottom-up path then *finds* it. There is no second creation path and therefore no duplicate to guard against.

As defence in depth, child instantiation is made idempotent regardless:

> **R-C11.** `InstantiateChildren(stage)` creates a repetition-0 instance only for `PlanItem`s that have **no** existing instance under that stage instance. Repetitions are created exclusively by the explicit `Instantiate(stage, item, rep>0)` operation.

And the satisfaction itself cannot be lost between "target has no instance" and "target has an instance" — this is #177 in a new guise — because the occurrence is recorded on the sentry *before* the activation and consumed *after*, both inside one turn and one journal write.

**4. `Disabled` ancestors: refuse, do not auto re-enable.** Today's ancestor ladder (`StageBehavior.cs:578-585`) fires `Reenable` from Disabled and `ManualStart` from Enabled. `ManualStart` is defensible — Table 8.7 says the chain "moves into Active state", which necessarily overrides the ManualActivationRule. `Reenable` is not: Table 8.8's `re-enable` row (printed p.116) reads "Transition by a `Case` worker (human) decision", and auto-firing it silently overrides an explicit human decision to exclude that stage from this case.

**Design: refuse.** Journal `BottomUpActivationRefused { blockingInstanceId, blockingState }`, log at Warning, and leave the satisfaction recorded (so it can still be consumed if a Case worker later re-enables the stage). This is a **behavior change** from today and should be flagged in review as such.

**5. Lint.** With the fix, cross-stage `sourceRef`s work, so no "unsupported" lint rule is needed. What *is* needed is a rule for a `sourceRef` that resolves to no `PlanItem` at all — currently such a model deploys clean and silently never fires.

### C.12 Planning, roles, and the ApplicabilityRule (phase P6)

P6 previously appeared in the phase table and the deleted-grains table with **no design content anywhere** — `GetPlannableItems` and `PlanDiscretionaryItem` were named on the new grain surface, `IPlanningTableGrain` and `IRoleGrain` were marked deleted, `Authorize` was reduced to "an in-memory check" with no statement of what it checks, and Table 8.13 appeared only as a row in the citation appendix. A phase with a deliverable and no specification is a phase that gets designed during implementation. This section closes that.

**Spec, verbatim.** §8.7 (printed p.124):

> Table 8.13 — Planning constrained to Case, Stage, and Task instance lifecycles
>
> | Contain a Planning Table | States for which planning is allow |
> |---|---|
> | casePlanModel | Active, Failed, Suspended, Completed, Terminated |
> | Stage instance | Active, Available, Enabled, Disabled, Failed, Suspended |
> | HumanTask instance | Active |
>
> "If a `Stage` instance is in Active state, then the planned `PlanItems` are instantiated immediately after planning completes. If the `Stage` instance is in another valid planning state, the planned `PlanItems` are instantiated when the `Stage` instance transitions to Active state. When a `Stage` instance has a `PlanningTable`, the `TableItems` of that `PlanningTable` can be used for planning. The resulting instances of the planning MUST be added to the `Stage` instance.
>
> `Case` workers planning at a particular `HumanTask` instance are constrained to use the `PlanningTable` for that `HumanTask` instance. The resulting instances of the planning MUST be added to the parent `Stage` instance of the `HumanTask` instance. Those planned `PlanFragments`, `Stages`, or `Tasks` are instantiated immediately after planning completes (because the parent `Stage` instance in which the planning task is taking place is in Active state)."

§8.6.5 (printed p.124):

> "This rule is evaluated and used for planning. It impacts planning by a `HumanTask` or into a `Stage` instance. During planning the only `DiscretionaryItems` that MUST be shown to the `Case` Worker (in the `authorizedRoleRef`) are those, for which the `ApplicabilityRule` evaluates to TRUE."

**Four things that table settles, each of which the redesign must implement.**

1. **The three element kinds have three different permitted-state sets, and none of them is "Active only".** A Stage instance can be planned into while `Available`, `Enabled`, `Disabled`, `Failed` or `Suspended`; the casePlanModel can be planned into while `Completed` or `Terminated`. A HumanTask is the only one restricted to `Active`. This is the same shape as §C.5's allowlist and is implemented the same way — a static `(elementKind, state) → bool` table, not a ladder.
2. **Planning is deferred, not refused, outside `Active`.** "the planned `PlanItems` are instantiated when the `Stage` instance transitions to Active state." That is a **fourth `DeferredEffect` case** and it reuses §C.6's mechanism directly: `PlanDiscretionaryItem` against a non-Active Stage appends `DeferredEffect { Kind = PlannedItemInstantiation }` and journals `EffectDeferred`; the Stage's `start` / `manual start` / `parent resume` into Active drains it through the ordinary `Instantiate` operation. The membership decision (the item *is* planned) is durable immediately; only the instantiation waits.
3. **Planning at a HumanTask adds to the HumanTask's parent Stage, not to the HumanTask.** A HumanTask is not a container. `PlanDiscretionaryItem(humanTaskInstanceId, …)` therefore resolves its target stage as `owner.Parent`, and because a HumanTask is only plannable while `Active` its parent is necessarily `Active` too — which is why the spec can say those instantiate immediately with no deferral branch.
4. **`GetPlannableItems` is a filtered read, and the filter has two independent conjuncts.** An item is returned only if (a) its `ApplicabilityRule` evaluates TRUE — an expression evaluation, so `GetPlannableItems` is the one *read* on `ICaseGrain` that is not a pure projection and must go through `Prepare` like any other rule; and (b) the calling actor holds a role in the `DiscretionaryItem`'s `authorizedRoleRefs`. §8.6.5 states both in one sentence ("MUST be shown to the `Case` Worker (in the `authorizedRoleRef`) … for which the `ApplicabilityRule` evaluates to TRUE"). An absent `ApplicabilityRule` is TRUE (the rule is optional; nothing restricts what it does not constrain). An empty `authorizedRoleRefs` is unrestricted.

**What replaces `IRoleGrain`.** Case roles are part of the pinned definition (§A.5) — `caseRoles` is a static property of the `Case`, and role *assignment* is per-case state (`CaseRuntime.Roles`, already in §A.2). So `Authorize` becomes: resolve the actor from the command preamble's explicit `Actor` (§D.5.2), look up its assigned role ids in `CaseRuntime.Roles`, and intersect with the required `authorizedRoleRefs`. Two call sites and only two: `GetPlannableItems` / `PlanDiscretionaryItem` (this section) and `Trigger(occur)` on a `UserEventListener` (§C.5 outcome 3). No grain, no round trip, no ambient context.

**Scope boundary, stated so it is not mistaken for a gap.** [04](04-adversarial-review-2026-08.md)'s finding **I5** — that the authorization model has substantive gaps beyond plumbing (no authentication story, `authorizedRoleRefs` unenforced on several paths, role assignment unexposed) — remains **orthogonal and out of scope**. What P6 owns is that the redesign does not *lose* the enforcement points that exist, and that the two above are specified rather than inherited by accident. If I5 is scheduled later it changes what `Authorize` checks, not where it is called.

**P6's acceptance is the discretionary corpus subset**, plus one new characterization: a discretionary item planned into a `Suspended` Stage must instantiate on `resume` and not before. That scenario does not exist today and should be written in P0 against the old engine if the old engine has any answer at all, or written fresh in P6 if it does not.

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

**A CMMN `PT15S` timer is implementable with reminders alone.**

> **Prerequisite — not polish, and nothing else in P5 works without it.** `MinimumReminderPeriod` **must** be configured to `TimeSpan.FromSeconds(1)` explicitly, in both the Development and Deployed silo paths, **before** the first PT15S timer is registered. Today the repo configures `ReminderOptions` **nowhere** — grep returns zero hits — so both paths run at the one-minute default and `RegisterOrUpdateReminder` throws `ArgumentException` on registration, client-side, inside the calling grain (§D.1.2). This was previously item 1 of a five-item list, which read as an optimisation. It is a hard precondition: it is the single line that decides whether P5 can ship at all.

The rest of the design:

1. **One reminder per armed timer**, named deterministically: `timer:{planItemInstanceId}:{occurrenceIndex}`. Not one shared keep-alive reminder — the per-timer name is what makes reconciliation (below) possible.
2. **Non-repeating reminders.** Register with `dueTime = max(NextFireUtc − now, MinimumReminderDueTime)`, and `period` set to a large sentinel (Orleans requires a non-infinite period). `ReceiveReminder` unregisters the reminder as its first act, then processes the tick and — if `RemainingOccurrences > 0` — registers the next one. Rationale: CMMN intervals are calendar-arithmetic-derived (§C.7c) and are not always a constant `TimeSpan`; `P1M` is not a fixed period, so a repeating Orleans reminder cannot express it. Computing each next fire explicitly is the only correct handling and costs one reminder-table write per fire.
   - **The `max(...)` clamp is required, not defensive.** Step 3 of `ReceiveReminder` unregisters before the `TimerFired` event is journalled and confirmed. A crash in that window leaves `CaseRuntime.Timers` holding a `NextFireUtc` **in the past** on the next activation, so reconciliation computes a negative `dueTime` and `RegisterOrUpdateReminder` throws — turning a recoverable crash into a case that cannot activate. `MinimumReminderDueTime` is Orleans' own 1 ms floor (§D.1.3); clamping to it means an overdue timer fires immediately on recovery, which is the correct semantics anyway (the time genuinely elapsed — §C.6(c)).
3. **Reconciliation — this replaces the outbox — runs on activation *and* at the end of every turn.** It calls `this.GetReminders()`, diffs the live set against `CaseRuntime.Timers`, registers anything missing, and unregisters anything orphaned. Because `RegisterOrUpdateReminder` is idempotent and reminders are enumerable, this is self-healing regardless of where a crash landed relative to the journal confirm. It is also the fix for **C2** (pending timers silently lost on restart) — which under the current design is unfixable without exactly this reconciliation, because `TimerEventSchedulerGrain.OnActivateAsync` never re-reads its own durably-persisted `State.JobKeys`.
   - **Activation-only would not have been enough** (§B.1 step 6): the grain stays activated after a turn, so a failed reconcile would go uncorrected until the next deactivation, and `CommandId` idempotency lets a client retry return before ever reaching the step.
   - **Unverified prerequisite.** No production code in this repo calls `this.GetReminders()` today — the only reminder usage is `TimerEventSchedulerGrain`'s keep-alive `RegisterOrUpdateReminder` — so whether `GetReminders()` is safe to await inside `OnActivateAsync` on Orleans 10.2.1 (as opposed to deadlocking against the activation turn, or returning before the reminder service has loaded the grain's entries) has **not** been established. Everything else in §D.1 was verified against a live silo; this was not. **Verify it before P5 starts**, and if it is unsafe in `OnActivateAsync`, the fallback is to reconcile on the first turn after activation instead, which costs one turn of staleness and nothing else.
4. **Delete the 118-minute keep-alive reminder** (`TimerEventSchedulerGrain.cs:36-41`). There are zero `UnregisterReminder` calls anywhere in `src/`, so today every case that ever scheduled a timer keeps a grain re-activating every ~2 hours forever, including closed cases — unbounded reminder-table growth. With per-timer reminders, a case with no armed timers holds no reminders and is free to deactivate.

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

**Key it by a constant.** `IExpressionGrain` is `IGrainWithGuidKey` and every production call site keys it by `Host.CaseInstanceId` (`BaseBehavior.cs:322`, `TimerEventListenerBehavior.cs:225`, `PlanningTableGrain.cs:105`, `SentryGrain.cs:382`). Combined with `[StatelessWorker]` on the implementation, that yields **one activation per case per silo** — and after I-4 the grain is a pure function of its request, so those activations are indistinguishable from one another and hold nothing worth partitioning. The case id in the key buys no isolation, no locality and no cache affinity; it only fragments the StatelessWorker pool and defeats its per-silo activation limit. **Design: a constant key (`Guid.Empty`).** One line, and it is the difference between a worker pool sized by silo and a worker pool sized by live case count.

**This closes D3 entirely, not just edge A.** Edge B — `CaseFileItemGrain.EnsureCaseNotClosed` calling `ICaseGrain` on every case-file mutation (`CaseFileItemGrain.cs:290-304`) — disappears too, because case-file mutation is now a method on the case grain and the Closed check is a local field read. The distributed wait cycle has no edges left.

**What it means for `contextRef` and I4.**

**Spec, verbatim.** Table 5.32, `IfPart.contextRef` (printed p.36), and identically Table 5.54, `RepetitionRule.contextRef` (printed p.55):

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

**OQ-9 (was an open question; now a measurement task with a sequencing hazard attached).** Should `ExpressionGrain` remain a grain at all? Once it is a pure function it could be an injected singleton service running in-process. Keeping it a `[StatelessWorker]` releases the case grain's thread during heavy Jint work — but a StatelessWorker call still costs a local message hop plus serialization of the bound JSON, which for a small context may exceed the Jint cost outright. A policy split (in-process below a size/complexity threshold, offload above it) is a third option.

This is **measurable today, with the harness that already exists.** `ExpressionEvaluationBenchmarks.cs` gives all three of the relevant numbers directly: `EvaluateAsWired` (the production shape — fresh engine and fresh executable per call, `[Benchmark(Baseline = true)]`), `EvaluateWithReusedEngine`, and `EvaluateOnWarmEngine` (the evaluation-only floor); `JintEngineBenchmarks.cs` isolates engine construction (#224). Everything needed to compare "Jint cost" against "local hop + serialization" is in `src/Wayfinder.Benchmarks/Expressions/`.

> **Sequencing hazard — this is the part that matters more than the answer.** **P1 is declared independently shippable and it rewrites `IExpressionGrain`'s contract.** If OQ-9 later resolves to "in-process service", the grain interface P1 just rewrote is deleted and that portion of P1 is wasted work — including its Orleans serialization attributes, its `[StatelessWorker]` wiring, and the architecture test written against `IGrainFactory` in a grain that no longer exists. Two acceptable orders, and the choice must be made **before P1 starts**: either (a) run the benchmarks now and settle OQ-9 first, or (b) reorder P1 so that the *bound-request shape* (`ExpressionRequest`, the `contextRef` binding, I4) lands as a plain type and a plain method on whatever hosts it, with the grain-vs-service question deferred behind that seam. Option (b) is cheap and is the recommendation if measuring now is inconvenient; what is not acceptable is shipping P1 with the question still open, which is what the phase list currently implies. This is also the single biggest lever on turn latency (§D.6).

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

**Design.** Each `CaseFileItemInstance` carries a **per-item logical version counter**, incremented on every one of its own Table 8.2 transitions (`create`, `update`, `replace`, `addChild`, `removeChild`, `addReference`, `removeReference`, `delete` — Table 8.2, printed p.108) and stamped onto each of its journal events. `GetCaseFileItemValueAt(itemId, version)` folds the case journal filtered to that item's events up to that logical version. The route, the parameter, and the meaning ("the Nth operation on this item") are preserved.

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

`Conformance/COVERAGE.md` is also stale, but **less wrong than an earlier draft made it look**, and the distinction matters because it changes what has to be regenerated. The file is an append-only log of successive verification passes, and the quarantine line quoted previously (`:39`, "33 scenarios — 25 executed green, 8 quarantined") is **superseded later in the same file** by `:80-81`: "the suite is now 33/33 green, 0 skipped." Quoting the earlier line as the current claim implies a quarantine protocol that the file itself already records as retired. (`:22` carries an earlier pass still: "23 executed green, 10 quarantined".)

The **durable** staleness is the scenario count: every pass records **33**, and the code now has **41** `[Fact]`s across 5 scenario files. That is the number to fix. Two smaller ones travel with it: `:57` and `:76` still name `Flow.Grains.Tests` / `Flow.Grains.Tests.Integration`, stale since the `Flow.* → Wayfinder.*` rename (#146). Regenerating the file is part of P8, and the regeneration should replace the append-only log with a single current-state section — the log format is what let three mutually contradictory counts coexist in one document.

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

**Every resident structure must be bounded by something other than case age**, and one of them was not. The enumeration, so the property is checkable rather than assumed:

| Structure | Bounded by |
|---|---|
| `InstancesById` | plan items × repetitions, capped by the #67 ceiling |
| `SentryInstance.Pending` | one entry per OnPart of one sentry — model-bounded, and entries are overwritten, never appended |
| `SentryInstance.SatisfactionCount` | a `long` (§C.8) — **this was an unbounded `List<SatisfactionRecord>` in an earlier draft**, growing once per satisfaction, i.e. once per case-file `update` in the common case |
| `PendingWhileSuspended` | drained on `resume` / `parent resume`; bounded by effects earned during one suspension |
| `Timers` | one per armed TimerEventListener instance; removed at `TimerDisarmed` |
| recent-`CommandId` ring | fixed size (§D.6.3) |
| `CaseFile` | case-file items, which are created ad hoc (#16) and are not otherwise bounded — the one genuinely open-ended structure, and it is user data rather than engine bookkeeping |

The journal itself remains unbounded by design and is #105's problem (§D.3.3), not this table's.

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

**A caller-side timeout does not roll back the turn.** The grain keeps working and confirms.

**This is the same fact §B.4.4 depends on, and the two sections must be read together.** A divergent cascade is the worst case of everything above: it burns the case grain's only thread, exceeds the response timeout, **keeps running after the caller has given up**, blocks every other request for that case for the duration, and then confirms one very large journal write. Today the same model diverges cheaply across many turns and the #67 ceiling stops it early. That regression is stated in full at the end of §B.4.4; the point here is that mitigation 3 above is not a tidiness measure — `MaxOperationsPerTurn` is the *only* thing standing between a pathological model and a case grain that is unavailable for as long as the divergence runs. Therefore:

#### D.6.3 Idempotency is a new, required property

If a client retries a command after a timeout, the command must not double-apply. Design: every mutating method takes an optional caller-supplied `CommandId`; `CommandReceived` records it; the preamble detects a duplicate and returns without touching the agenda. A bounded recent-command-id ring is kept in state (say the last 64), which is sufficient for retry windows and does not grow.

**Two things an earlier draft got wrong here, both of which are contract, not detail.**

1. **The ring must be folded from a replayed event.** It is "kept in state", so it must be reconstructed by replay like everything else in §B.5.2 — which means `CommandReceived` cannot be a non-replayed audit-only event. §B.5.1 now folds it, for `commandId` alone. Without that the ring is empty after every activation and the first retry across a deactivation double-applies, which is exactly the case this section exists to prevent.
2. **A duplicate returns the *current* snapshot, not the original turn's.** An earlier draft promised "the snapshot produced by the original turn". Nothing stores snapshots — §B.5.2 is billed as exhaustive and has no such entry, and adding one would mean persisting a projection alongside the events it is derived from, which is precisely the redundancy the log-storage model exists to avoid. **The real contract is weaker and must be stated as such: a duplicate `CommandId` returns the case's current post-fixpoint snapshot.** If other commands landed in between, that snapshot reflects them too. It is still a correct answer to "what is the state of this case, and did my command apply?" — the command did apply, and the state is current — but it is *not* a replay of the original response, and a caller diffing two responses to the same `CommandId` must not assume they are equal. Reconstructing the original response would require either storing it or re-folding the journal to the recorded `TurnCompleted` boundary; the second is possible (`TurnCompleted` carries `journalPositionRange`) and is deliberately **not** done, because no caller needs it and it would put a replay on the hot path of every retry.

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

**P2–P7 — the collapse. The forced-atomic core is smaller than an earlier draft claimed.**

An earlier draft said "P2–P7 are not independently shippable, and this document will not pretend otherwise", and then in the next breath gave P2 a verification column naming an in-process scenario runner. Those two statements cannot both be right, and the second one is the true one. Overstating the atomic core is the **largest schedule risk in this document** — it converts a sequence of reviewable increments into one long-lived branch and one enormous cutover PR, which is the shape of change most likely to slip and hardest to review. Three seams exist, and each is worth taking:

**Seam 1 — verification starts at P2, not P7.** P2's own verification column specifies "a scenario runner that drives `.cmmn` samples through the new engine directly, in-process". If that runner exists at P2, then the corpus can be run against the dark engine **progressively from P2 onward** — a growing subset, tracked as a number that goes up every phase. The claim "nothing is verifiable until the cutover" is therefore false, and the document had already given itself the answer. Build the runner *first*, in P2, and make "scenarios green against the dark engine" a per-phase metric. That alone removes most of the risk of a long-lived branch, because the branch stops being unverified.

**Seam 2 — the forced-atomic core is P2 + P3 + P7, not P2–P7.** What forces atomicity is the D3 wait cycle, and D3's cycle is `ExpressionGrain → CaseFileItemGrain` — a *callback into* a case-owned grain. A new `CaseGrain` that calls `ICaseFileItemGrain` **outward** is not a cycle and does not deadlock. So the collapse of the case file (P4), timers (P5) and planning (P6) can each land **after** cutover, with the new `CaseGrain` delegating outward to the surviving grain in the interim. Concretely:

| Phase | Forced into the atomic core? | Why |
|---|---|---|
| P2 — object model, agenda loop, cascades, Table 8.12 | **Yes** | this *is* the collapse |
| P3 — sentry algebra, bottom-up | **Yes** | sentries have no meaning outside the loop; D-C8-1 changes their identity |
| P4 — case file | No | `CaseGrain → ICaseFileItemGrain` is an outward call, not a cycle. Costs a grain hop per case-file read until it lands |
| P5 — timers | No | reminders are already case-keyed; `ITimerEventSchedulerGrain` survives one more phase |
| P6 — planning | No | `IPlanningTableGrain` is read-mostly and off the loop's hot path |
| P7 — cutover | **Yes** | by definition |

That is a materially smaller thing to hold on a branch, and P4/P5/P6 become ordinary post-cutover PRs with the whole corpus green behind them.

**Seam 3 — OQ-11 was argued against the wrong alternative.** See below.

What remains genuinely atomic is built **dark** (implemented and unit-tested, but not registered in the silo and not resolved by any test), as a sequence of small PRs each with its own tests, ending in one cutover PR.

| | Scope | Its own verification |
|---|---|---|
| P2 | **The in-process scenario runner, first.** Then: object model, agenda loop (§B), Tables 8.6/8.8/8.9/8.11 cascades, Table 8.6-vs-8.12 completion (§C.2), **M1, M2, M4, M5** | Unit tests over the runtime object graph, plus **a scenario count**: how many of the 41 drive green through the runner. That number is reported every phase from here on and must never go down |
| P3 | Sentry algebra (§C.8: **C4, M8, M9, M10, I7**), **M6a/b**, cross-stage + bottom-up (**M11**) | The sentry-focused corpus subset, run against the dark engine |
| P4 | Case file inside the case grain, incl. version-number compatibility (§D.4.3) | P0's characterization test, re-pointed |
| P5 | Timers on reminders (**M7a/b/c**, **M6c**); `MinimumReminderPeriod` config (§D.1) | Timer scenarios via the injectable clock (§F.3) |
| P6 | Planning table, roles, `GetPlannableItems`, `PlanDiscretionaryItem` — **specified in §C.12** (Table 8.13, §8.6.5 ApplicabilityRule, the `Authorize` check, deferred instantiation outside Active) | Discretionary corpus scenarios, plus the new "planned into a Suspended Stage instantiates on resume" characterization (§C.12) |
| P7 | **Cutover.** Register the new grain; delete `PlanItemGrain`, `SentryGrain`, `CaseFileItemGrain`, `PlanningTableGrain`, `RoleGrain`, `CmmnElementGrain`, `Scheduler/`, `Infrastructure/Quartz/`, all stream types and `StreamFlags`; adapt the corpus harness (§D.4.4); **delete every wait helper (§F.4)**; land the event-schema-versioning convention (§E.2). | **The whole corpus, deterministically, N consecutive runs** |

**P8 — API surface (independently shippable after P7).**
Widen `CaseSnapshot`; simplify `CaseViewProjector` to a pure mapping; drop `Wayfinder.Application` → `Wayfinder.Grains`; add `InstanceId`/`Repetition` to `PlanItemView`; narrow the wire transition enum (§C.5); regenerate `COVERAGE.md`.

**P9 — Deferred.** Journal snapshotting → #105 (§D.3.3).

> **Open question OQ-11 — blocking, and it must be settled first because it determines the branch strategy for everything else.**
>
> An earlier draft compared the dark branch against the *worst* flag design — "registering both engines behind a per-case feature flag and migrating the corpus scenario by scenario", with two `ICaseGrain` implementations in one silo, doubled fixtures, and a flag that outlives the migration — and rejected it. That is a false dichotomy: it rejects a strawman and leaves the dark branch unexamined.
>
> **The cheap version, which is what OQ-11 should actually be argued against:** a **second interface** (`ICaseGrainV2`, its own grain type, its own key space), with the engine chosen **at `Create`, per case, for new cases only, and no migration path at all**. Old cases keep running on the old grain until they are abandoned; new cases land on the new one. There is no flag inside either engine, no dual-write, no shared fixture, and nothing to unwind — the "flag" is which grain type `Create` resolved, recorded once, immutable for that case's life.
>
> The repo supports this specifically, and the evidence is already in this document: §E.1 establishes there is **no production traffic and no durable data** beyond disposable local volumes, so abandoning old cases costs nothing; and §A.4/§D.4.1 establish that `Wayfinder.Api` makes **zero direct grain calls** with only **three production call sites** touching plan-item or case-file grains, so the seam where the choice is made is three files wide. The usual reason a per-case flag is expensive — production cases you cannot abandon and must migrate — does not apply here.
>
> Re-argue OQ-11 against **that** option. The honest comparison is: (a) dark branch — one big final PR, nothing running until it lands, but no duplicate registration; versus (b) second interface — both engines registered and both exercisable from the corpus from P2 onward, at the cost of one extra grain type registered in the silo for the duration and a deletion PR at the end. Given Seam 1 above, (b) also makes the growing-green-scenario-count metric *observable through the real API surface* rather than only through an in-process runner. Reviewers should note that the earlier recommendation of (a) rested on an argument — "no production traffic to protect, which is the usual reason to pay for a flag" — that cuts at least as strongly in favour of (b), because it is also the reason the flag here is cheap.

---

## F. Acceptance criteria

**The redesign is done when the conformance corpus — 31 `.cmmn` samples driving 41 scenarios — passes deterministically with every polling and scaled-wait helper deleted.**

### F.1 What "deterministically" means operationally

Three properties, each of which must be true for the phrase to mean anything:

> **F-1.** Every mutating grain call returns only after the agenda has drained to fixpoint **and** the journal has been confirmed. (§B.1 steps 4–5.)
>
> **F-2.** A test therefore asserts on the **returned snapshot**, immediately. No polling, no delay, no timeout, no retry. If a state is not in the returned snapshot, **the engine will not produce it in response to that call** — the call's effects are complete and closed at the moment it returns.
>
> **F-3.** A "must NOT happen" assertion is a plain assertion on the returned snapshot. This is the property that matters most, because it is the one the current architecture cannot provide at any price.

**F-2's scope is the call, and an earlier draft said "ever".** That is false, and F-3's negative assertions would have inherited an unsound premise from it. Two mechanisms legitimately produce state after a call returns, and neither is a defect:

- **Reminder-driven turns.** A timer armed during a call fires in a *later, separate* turn (§C.7, §F.3). "Task X never becomes Active" is not assertable from one snapshot if a timer is pending; it is assertable after the timer-driver seam has been advanced past the fire instant.
- **Effects deferred under suspension.** R-C6 (§C.6) holds a satisfaction, a repetition request or a timer tick until `resume` / `parent resume`. The state is *determined* at the original call and *produced* at the later one, and §D.4.2's `CaseSnapshot` shows the deferral, so a test can assert on the deferral rather than on its absence.

The correct formulation is the one F-2 now carries: **closed with respect to that call.** Every "must NOT happen" assertion must therefore either (a) run on a case with no armed timers and no suspended ancestors, or (b) advance the timer driver and drain the suspension first, and then assert. That is a real constraint on how the negative scenarios are written, and it should be checked scenario-by-scenario when `PollUntil` is deleted at P7 — a `PollUntil` that was silently absorbing a reminder turn will not convert into a bare assertion.

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
| **`exitCriterionRef` OnParts end-to-end** (#82's D10 work) | **Preserved.** The Stateless parameterized-`Exit` mechanism that threads the criterion id becomes unnecessary — the agenda operation carries it directly. Simplification, not loss. (`sentryRef` is not a CMMN 1.1 attribute and never was implementable; see §C.8.) |
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

§10 records it as "sound against interleaving, and the pattern worth keeping where cross-grain edges remain".

**The qualifier is *not* vacuously satisfied, and an earlier draft claimed it was.** Cross-grain edges survive this redesign — three of them:

| Edge | When | Nature |
|---|---|---|
| `CaseGrain → IExpressionGrain` | mid-turn, in every `Prepare` (§B.3) | inside the turn; the case grain is blocked on it |
| `CaseGrain → ICaseDefinitionGrain` | once, at `Create` (§A.5) | before any state exists to be inconsistent |
| `CaseGrain → the reminder service` | §B.1 step 6, **after** the confirm | **a determined-but-not-yet-effected side effect crossing a boundary** |

The third one is *exactly* the situation #198's blocking/clearing signal was built for: the journal says a timer is armed, the reminder table does not yet agree, and the two are reconciled by a second act that can fail independently. The claim "there are no cross-grain edges inside a case" is true only of edges *within the case's own state*, and that is a narrower statement than the qualifier §10 wrote.

What actually justifies the deletion is the second argument, which stands on its own and does not need the first: **the interval #198 exists to cover ceases to exist.** The repetition determination and the successor's creation happen in the same turn, in the same journal write, with the agenda's FIFO-before-deferred ordering (§B.2) guaranteeing the instantiation runs before the completion check. There is no window in which a determination is durable and its consequence is not.

And the surviving edge that *does* have that shape — step 6 — is defended by a different and simpler mechanism than a blocking flag: **idempotent reconciliation against an enumerable external set** (§D.1.4). That works because Orleans reminders can be listed and re-registered without harm, which is not a property the #198 gate could have relied on for stream deliveries. So the pattern is not being deleted because nothing needs it; it is being deleted because the one thing that still needs it has a better answer available.

What must be preserved is the **invariant**, not the mechanism:

> No stage completes over a determined-but-uncreated successor.

Enforced now by ordering rather than by a durable blocking flag. Deleted with it: `RepetitionRequestsAwaitingResolution`, `SettleRepetitionRequest`, `TryCompleteStageAfterRepetitionResolved`, `RepetitionRequestSettled`, `RepetitionBuffered`, `RepetitionBufferDrained`, `DrainPendingRepetitions`, `SettleStrandedPendingRepetitions`, and `StageBehaviorStore._settledRepetitionSourceInstanceIds`.

This should be reviewed carefully. It is ~600 lines of hard-won, well-tested logic being deleted on the argument that its precondition no longer holds. If that argument is wrong anywhere, this is where the redesign breaks.

**2. `CmmnElementGrain` dissolves as an abstraction.**

It is the shared base for `CaseGrain`, `PlanItemGrain`, `CaseFileItemGrain`, `SentryGrain`, `PlanningTableGrain`, and `RoleGrain`, and it carries the single `[LogConsistencyProvider]` attribute, the `RaiseEvent` actor-stamping shadow (#59), the `GetJournaledEvents` seam, the dotted-address decomposition, and the whole subscription contract. Only the case remains a grain, so the base class has one subclass and no reason to exist. Its useful parts — actor stamping, the journal read seam — move onto the case grain directly, and `GetJournaledEvents()` must gain paging (§D.4.2), because "return the entire journal in one call" does not survive a single merged log.

> **One line in this deletion is silent and storage-affecting: carry the provider attribute across.** `[LogConsistencyProvider(ProviderName = "LogStorage")]` appears **exactly twice** in `src/` — `CmmnElementGrain.cs:20` and `AzuriteJournalTestGrain.cs:82`, the second being a test grain. Deleting `CmmnElementGrain` therefore removes the **only production declaration of the log-consistency provider in the codebase.** The new `CaseGrain` would then resolve whatever the unkeyed default is — no compile error, no startup error, no log line, and a different storage path. That is the *same latent bug* §D.3.1 already flags on `CaseDefinitionGrain` (`CaseDefinitionGrain.cs:17`, a `JournaledGrain` carrying no attribute, whose own comment at `:81` notes it is the other journaled root). Put `[LogConsistencyProvider(ProviderName = "LogStorage")]` on `CaseGrain` explicitly in the same PR that deletes the base class, and add an architecture test asserting that every `JournaledGrain` in `src/Wayfinder.Grains` carries one — which also closes the `CaseDefinitionGrain` case as a side effect.

**3. Per-element journals, and everything read through them.**

`ActorStampingIntegrationTests` and `CaseFileItemGrainTests` read a specific element's journal via `GetJournaledEvents()`. There is now one journal. Those tests must be re-expressed as filtered reads over the case journal. This is the mechanism behind §D.4.3's versioning constraint and is the only place the merge is user-visible.

### G.4 Two smaller preservation notes

- **`PlanItemSnapshot.BehaviorExtension` as `object`, with `StageBehaviorSnapshot.Children` (`planItemId → instanceId → repetition`) as the sole instance registry**, is replaced by a real tree on the snapshot (§D.4.2). The `is StageBehaviorSnapshot` type test that currently detects stage-ness goes with it.
- **`ITimerEventSchedulerGrain` is deleted but is the template for its own replacement**: case-keyed, plan-item instance id as a parameter. That shape is exactly what §D.4.2 adopts for the whole collapsed surface.

---

## H. Open questions, collected

An earlier draft presented twelve questions in one flat list. Five of them were not open at all — they were decisions already taken in the body with a dissent recorded alongside (OQ-1↔§B.4.4, OQ-3↔D-C2-1, OQ-4↔D-C3-1, OQ-5↔D-C6-1, OQ-7↔D-C8-1). Listing a made decision as "open" invites it to be re-opened mid-phase, which is the most expensive time to re-open anything. Two more (OQ-2, OQ-9) were not questions but unmeasured constants, with the benchmark harness already sitting in the repo.

The list is therefore split three ways by **what has to happen to it**, not by section order.

### H.1 Blocking — must be answered before the first implementation issue is cut

These change code that early phases write. Answering them late means rewriting that code.

| # | § | Must be settled before | The question, and what turns on it |
|---|---|---|---|
| **OQ-11** | E.3 | **Everything — answer this first** | Dark long-lived branch, or a **second grain interface** chosen at `Create`, per case, new cases only, no migration? §E.3 re-argues this against the cheap option rather than the strawman it was originally rejected against. It determines the branch strategy, the shape of every PR from P2 on, and whether scenarios are verifiable through the real API surface or only through an in-process runner. |
| **OQ-7** | C.8 | **P3 — but it is in the P2 object model** | Sentry instance per **criterion instance**, or shared per owning-stage instance? Per-criterion: fresh state per repetition, local evaluability gate, and the `forwardedSatisfaction` machinery of §C.6(a) that goes with it; costs duplicate IfPart evaluation. Shared: cheaper, reintroduces "satisfied for whom?". This is a **class in `CaseRuntime`** (§A.2), so it is written in P2 even though its consequences land in P3. Leaning: per-criterion. |
| **OQ-3** | C.2 | **P2** | Does a Suspended non-required child block `autoComplete`? Now genuinely two-sided: Table 8.7's `Active` description (printed p.114) — "contain at least one … instance in the Available, Enabled, Active, **Suspended** state" — is direct spec evidence for the dissent, and the earlier draft did not cite it. Changes `EvaluateCompletion`'s predicate **and** the expected final states of corpus scenarios, both of which are P2 deliverables. Provisional decision D-C2-1 is (a), complete-and-cascade. |
| **OQ-10** | D.5.3 | **P2** | Grain key: `(caseInstanceId, tenantId)` or keep `(caseInstanceId, casePlanModelId)`? Tenant-in-key makes cross-tenant access structurally impossible and retires the `"CPM"` landmine; it also invalidates every existing local dev blob (§E.2). This is the **key type of the grain P2 creates** — changing it later changes every fixture and every stored address. Leaning: tenant-in-key. |
| **OQ-6** | C.7c | **P5, but decide by P2 if the answer might be (iii)** | Time zone for calendar duration arithmetic and offset-less instants. (i) reject conforming input — no; (ii) assume UTC + lint warning; (iii) per-tenant configured zone. Only (iii) gets `P1M` and DST right for a real business calendar, and **(iii) is a data-model change**: a tenant-level setting that must be resolvable from inside the case grain without ambient context (R-D5), which means it is pinned at `Create` like everything else and therefore touches P1/P2, not just P5. If (ii) is chosen it is a P5-local decision; if (iii) is even possible it must be known early. Leaning: (ii) now, (iii) later — but "later" is expensive, so decide deliberately. |

### H.2 Decided, with the dissent recorded — safely deferrable, and **not** to be re-opened mid-phase

Each of these is a decision made in the body. They are listed so the dissent is not lost, not so the decision is revisited. Changing any of them is a contained change late in the schedule.

| # | § | Decision | Dissent worth keeping on record |
|---|---|---|---|
| **OQ-1** | B.4.4 | **Fault the case and confirm** on divergence — auditable, recoverable via `re-activate`. | The partial cascade becomes durable, so an operator who re-activates resumes from a state the model never intended. The alternative (abort without confirming) keeps the case clean but needs a second write target. Reversible: it is one branch in the divergence handler. |
| **OQ-4** | C.3 | **D-C3-1: refuse** the `reactivate` of a Failed child under a Completed parent, at the transition. | The refusal is an engine-invented guard; CMMN states none. Whether it belongs at the transition (`CanFire`) or only at the public surface is the sub-question, and this design takes the stricter reading. |
| **OQ-5** | C.6 | **D-C6-1: (α)** — one catch-up occurrence for a timer suspended across several periods, re-anchored to the resume instant. | A judgement call, not a spec reading. A model using a timer as an SLA counter wants (β). Possibly belongs as a per-model attribute rather than engine policy — additive later. |
| **OQ-8** | D.1.4 | Proceed with `MinimumReminderPeriod = 1 s`. | Two things unmeasured: reminder jitter under load on real hardware, and Azure Table throughput at one write per fire per case. Fallback is a documented minimum (e.g. 5 s) plus a lint rule on `timerExpression`s below it. Benchmark during P5; it changes a constant, not a design. |
| **OQ-12** | D.3.3 | **Split #105**: defer snapshotting to 0.3.0, pull event-schema versioning into P7. | None substantive — the split is bookkeeping. Recorded because P7's scope depends on it. |

### H.3 Closed — these were never questions

| # | § | Resolution |
|---|---|---|
| **OQ-2** | B.4.4 | `MaxOperationsPerTurn` / `MaxSignatureRepeats` are **measurable now**. `src/Wayfinder.Benchmarks/Expressions/` already contains `ExpressionEvaluationBenchmarks.cs` and `JintEngineBenchmarks.cs`, which measure exactly the cost that dominates per-operation time. Run them, divide `ResponseTimeout` by the result, write the arithmetic next to the constant. An action item, not a decision. |
| **OQ-9** | D.2 | Grain-vs-in-process for `ExpressionGrain` is **measurable with the same harness**. It carries a **sequencing hazard** that is the real content: P1 is declared independently shippable and rewrites `IExpressionGrain`; if this later resolves to "in-process service", part of P1 is wasted. Either measure it before P1, or reorder P1 so the bound-request shape lands behind a seam that does not commit to a grain. See §D.2. |

---

## Appendix — spec citation index

Every normative claim above, with its printed page. PDF page = printed + 18.

Four citations in an earlier draft were off by one and are corrected here; each correction is marked **(was …)**.

| Citation | Printed | Used in |
|---|---|---|
| Table 5.19 — TimerEventListener attributes (`timerExpression`, `timerStart`) | 26 | C.7a |
| Table 5.21 — PlanItemStartTrigger attributes | 27 | C.7a |
| Table 5.26 — Sentry attributes ("MUST have an IfPart or at least one OnPart") | 33 | C.8 |
| Table 5.28 — CaseFileItemOnPart (`sourceRef : CaseFileItem[1]`) | 34 | C.8/I7 |
| Table 5.29 — CaseFileItemTransition enumeration (8 values, 6 repeatable) | 34 | C.8/M9, C.8 (counter vs ledger) |
| Table 5.30 — PlanItemOnPart — **exactly three attributes**: `standardEvent`, `sourceRef [0..1]`, `exitCriterionRef [0..1]`. **No `sentryRef`**; the "When sentryRef is specified…" sentence is 1.0 residue in the prose | 35 | C.8/I7 |
| Table 5.31 — PlanItemTransition enumeration (17 values; no `parentTerminate` — an *interchange* omission, not a spec one) | 35-36 | C.5/I3 |
| Table 5.32 — IfPart attributes (`contextRef` default = caseFileModel) | 36 | D.2/I4 |
| Table 5.33 — Expression attributes (`language` default) | 36-37 | G.1 (I2, orthogonal) |
| Table 5.51 — PlanItemControl: Milestone/EventListener `defaultControl` **MUST NOT contain a ManualActivationRule**; **absent ManualActivationRule defaults to TRUE**; absent RequiredRule/RepetitionRule default FALSE | 52-53 | C.6(d) |
| §5.4.11.3 — RepetitionRule: "**every time** an entry criterion with an OnPart is satisfied … a new instance … is created and transition to Available" (**no state qualifier**); "EventListeners cannot have RepetitionRule … R\<n\>/ notation" | 54-55 | C.6(b), C.7b, B.4.3 |
| Table 5.54 — RepetitionRule attributes (`contextRef` default = caseFileModel) | **55** (was 54) | D.2 |
| Table 5.55 — Applicability of PlanItemControl rules (Milestone: ManualActivationRule **N/A**, RepetitionRule **Applicable**; EventListener: all N/A) | **55** (was 56) | C.6(d), C.7b |
| §8.3 / Table 8.1 — CaseFileItem states | 107 | D.4.3 |
| Table 8.2 — CaseFileItem instance transitions (8 transitions) | **108** (was 107) | D.4.3 |
| Table 8.6 — Case instance transitions: **`complete` names no actor** and states the Case-level criterion (set includes `Closed`; no autoComplete branch; no DiscretionaryItems clause) | 112 | C.2, C.5 |
| Table 8.6 — `re-activate`, `close` actors | 113 | C.5, B.4.4 |
| Figure 8.3 — Stage/Task lifecycle, history pseudo-state | 113 | A.2, B.5.2 |
| Table 8.7 — Stage/Task states; **bottom-up activation** (chain defined on the *target's* ancestry); **`Active` description names `Suspended` children** | 114 | C.11, C.2/OQ-3 |
| Table 8.8 — `create`, `enable` (**RequiredRule MUST re-evaluate**), `start`, `disabled`, `manual start`, `suspended`, `fault` | 115 | C.1, C.5, B.4.2 |
| Table 8.8 — `complete` (**names no actor except "HumanTask … completed by a human"**), `terminate` (repetition re-evaluation), **`exit` (six source states, "or when the parent Stage instance transitions to Terminate state")**, `resume`, `re-activated`, `re-enable` (**RequiredRule MUST re-evaluate**) | 116 | C.1, C.5, C.9a, C.11 |
| Table 8.8 — `parent suspend`, `parent resume`; Table 8.9 begins | 117 | C.3, C.6 |
| Table 8.9 — `suspend`, `fault` rows | 118 | C.4 |
| Table 8.9 — `complete` rows (**Transition = `N/A`, To = `<impossible>` — the absence of a cascade, not a mandate**), `exit`/`terminate` rows (**where the `exit` cascade is actually prescribed**), **Notes (1) and (2)** | 119 | C.2/D-C2-1, C.3, C.4, C.9a |
| Tables 8.10-8.11 — EventListener/Milestone states and transitions; `create` (RequiredRule for Milestone) | 120 | C.1, C.5 |
| Table 8.11 — `suspend`, `terminate`, **`occur`** (UserEventListener vs Milestone), `resume`, **`parent terminate` (a real spec transition)**; **no `enable`/`start` for a Milestone**; **§8.5 Sentry** incl. its **first paragraph** (only one criterion required; exit is Active→Terminated) | 121 | C.5, C.6(d), C.8, C.9 |
| Table 8.12 — Stage completion criteria; §8.6.1 ("SHOULD complete"); §8.6.2 ManualActivationRule; §8.6.3 RequiredRule ("if not present … considered FALSE") | 122 | C.1, C.2 |
| §8.6.4 — "**the new instance is created and because the entry criteria is satisfied it moves from the Available state to either Active or Enabled state depending on the ManualActivationRule**"; Examples 1 and 2 | **123** (was 122) | C.6(a), C.11, B.4.3, B.4.4 |
| §8.6.4 Example 2 concluded (three B and three A); §8.6.5 ApplicabilityRule; §8.7 Planning / Table 8.13 (per-kind plannable states) | 124 | B.4.3, C.12 |
