# Adversarial Architecture & Conformance Review — August 2026

**Date:** 2026-08-01
**Scope:** full engine — lifecycle state machines, stage semantics, sentries/criteria, behavior property rules and repetition, timers, distributed architecture, model interchange and expressions, dependency/architecture alternatives.
**Method:** seven independent adversarial review passes, each verifying claims **directly against the raw OMG CMMN 1.1 spec** (`formal-16-12-01.pdf` — not committed here; download from [omg.org/spec/CMMN](https://www.omg.org/spec/CMMN/)). Every finding below was verified against actual code before inclusion; findings that could not be confirmed are labeled *plausible*. Repo docs (including [03-cmmn-execution-semantics.md](03-cmmn-execution-semantics.md)) were treated as claims to check, and doc-vs-spec/doc-vs-code disagreements are themselves findings (§7).

> **PDF navigation correction:** printed page ≈ PDF page **− 18** (not − 16 as previously documented; verified independently three times). Clause 8 starts at printed p.107 ≈ PDF p.125.

---

## 1. Executive verdict

**Wayfinder has a faithful skeleton and an unsound nervous system.**

Everything the spec defines as a table is implemented with unusual precision: the three Stateless lifecycle configurations match Tables 8.6/8.8/8.11 nearly cell-for-cell (including fault non-propagation, exit-from-Failed, and CasePlanModel terminating via `terminate` not `exit`); the Table 8.9 cascade matrix honors the hard per-type asymmetries (Milestone/EventListener children surviving a completed parent); behavior-rule defaults are correct on every path including error paths; and the XML interchange layer's optional-attribute defaults match the XSD exactly. The #198 completion-gate fix was adversarially re-verified and **is sound against interleaving** — its same-turn ordering invariant holds.

Where conformance breaks is systematic: **almost every confirmed defect lives on a transport edge** — an event crossing an Orleans stream between grains, a timer crossing from Quartz into a grain, a process restart, or a delivery arriving while its target is Suspended. The spec describes synchronous, atomic, case-scoped semantics; the current architecture distributes those semantics across unordered, volatile channels, and the engine has been re-deriving the lost ordering one incident at a time (#161, #177, #178, #186, #198 are all members of one bug class).

Tally: **5 critical**, ~15 major, ~25 minor/note findings. The architecture assessment (§8) concludes the granularity — grain-per-plan-item — is the root cause of the dominant class, and recommends collapsing to one journaled grain per case instance. **That recommendation was accepted 2026-08-01 (§9, Decision D-2026-08-01) and is the next work item.**

---

## 2. Critical findings

All confirmed against code.

### C1. Silo restart permanently deafens every live case
Stream subscriptions live in the PubSub store, registered as `AddMemoryGrainStorage("PubSubStore")` in **both** the Development and Deployed paths (`src/Wayfinder.Silo/Program.cs:240-241`, shared via `ConfigureSharedOrleansProviders`), while grain journals are durable Azure blobs. Nearly every subscription re-arm site is **Resume-only**: `CmmnElementGrain.SubscribeTo` resumes only `if (handles.Any())` (`CmmnElementGrain.cs:127-157`); entry/exit criteria (`BaseBehavior.cs:69-78`), stage child/repetition streams (`StageBehavior.cs:87-99`), sentry OnPart sources (`SentryGrain.cs:112-120`), and timer streams (`TimerEventListenerBehavior.cs:31-60`) all follow this pattern. After a restart, journals rehydrate state but `GetAllSubscriptionHandles()` returns empty and the grain **never listens again — silently**, including for all future events. Only the parent-transition subscription (`Create|Resume`) self-heals.
**Consequence for docs:** [03 §8](03-cmmn-execution-semantics.md)'s "a durable stream provider would close that gap" is wrong as stated — closing it requires durable streams **and** durable PubSubStore **and** a re-subscription strategy for Resume-only sites **and** an outbox (C-adjacent finding D4).

### C2. All pending timers are silently lost on restart
Quartz runs `RAMJobStore` in production (`Infrastructure/Quartz/QuartzSchedulerConfig.cs:50`, registered at `Program.cs:513`). `TimerEventSchedulerGrain` durably persists `State.JobKeys` (`Scheduler/TimerEventSchedulerGrain.cs:48-55`) but `OnActivateAsync` (lines 29-46) never re-reads them to reschedule — after a restart the durable keys point at jobs that exist nowhere, and nothing detects it. A TimerEventListener in Available waits forever. Additional confirmed defects in the same grain: `ScheduleTimer`'s `Task.WhenAll(WriteStateAsync(), ScheduleJob(...))` is non-atomic; cross-silo activation moves make `CancelTimer` a silent no-op against the wrong silo's scheduler; and the 118-minute keepalive reminder is **never unregistered** (zero `UnregisterReminder` calls in `src/`) — every case that ever scheduled a timer keeps its scheduler grain re-activating every ~2 hours forever, including closed cases (unbounded reminder-table growth).

### C3. Timer-originated cascades crash on `CaseRequestContext.TenantId`
`TimerTickJob.Execute` publishes from a Quartz worker thread with no Orleans `RequestContext` (`Scheduler/TimerTickJob.cs:24-59`). `CaseRequestContext.TenantId` **throws** when unset (`Wayfinder.Grains.Interfaces/CaseRequestContext.cs:24`), and `PlanItemGrain.DefineRepetition` reads it (`PlanItemGrain.cs:111-112`). Confirmed paths: (a) tick → Occur → sentry → stage `Start` → `HandleEnterActiveFromStart` → `CreateChild` → `DefineRepetition` → throw, with the stage's transition already durably confirmed — the stage is **wedged Active with zero children** while the stream agent retries a deterministically-failing handler and then drops; (b) a task completed by a timer-driven chain publishes its repetition request in an empty-context turn — the parent's spawn throws before `ChildRepeated` is raised, the request never settles, and the #198 gate holds the parent's Complete **forever** (the residual-window analysis at `StageBehavior.cs:989-1005` assumes lost delivery, not deterministic throw). No test covers tick-driven child creation. Note this is the flip side of a *correct* fail-closed tenant posture — the fix is stamping context at the timer boundary, not weakening the throw.

### C4. A satisfied sentry re-fires even when its IfPart evaluates FALSE
`SentryGrain.cs:344-375`: after the OnPart AND-join/IfPart block, the publish gate is `if (!State.Satisfied) return;` (line 367), and `SentryStore.Satisfied` is a monotonic latch (`SentryStore.cs:75-79`, never cleared). For a single-OnPart + IfPart sentry on a repeating source: occurrence 1 with IfPart TRUE latches and publishes (correct); every later occurrence publishes **regardless of the IfPart result** — the FALSE evaluation raises `OnPartNotRearmed` but execution falls through to the latched publish. `on TaskB.complete if amount > 100` stops checking the condition after its first success. Violates §8.5 (printed p.121): satisfaction requires all OnParts **and** IfPart TRUE. Fix shape: gate on "did *this turn* raise Satisfied," not the latched flag.

### C5. Every Stage grain reactivation creates a duplicate live sentry set
`StageBehavior.Define()` runs on every activation (via `PlanItemGrain.OnActivateAsync` → configurator → `BaseBehavior.Activate` → `Define()` when already defined) and mints `ShortGuid.NewGuid()` per sentry (`StageBehavior.cs:102-131`); the `if (await sentryGrain.Defined()) return;` guard can never hit for a fresh random id (the `// TODO: should raise an event to catalog sentry instance?` at line 128 is the missing piece). Each duplicate SentryGrain subscribes to the same OnPart sources (fresh grain → no handles → always subscribes) and publishes on the same definition-keyed satisfaction stream. After N routine idle-reactivations, **one source transition yields N satisfaction events** — e.g. phantom repetition instances — plus an unbounded journaled-grain/subscription leak.

---

## 3. Major findings — execution semantics

### M1. RequiredRule never re-evaluated on `enable`/`re-enable` (Table 8.8 MUST)
Table 8.8's `enable` row (printed p.115): "If the RequiredRule Boolean expression exists and the current value is FALSE, then it MUST be re-evaluated in this transition"; repeated verbatim for `re-enable`. `EvaluateRequiredRule` is called only from the three Create paths (`StageBehavior.cs:186`, `TaskBehavior.cs:66`, `MilestoneBehavior.cs:53`); no `OnEntryFrom(Enable/Reenable)` registration exists. A requiredRule over case-file data that was FALSE at instantiation and TRUE by enable time is ignored — the parent completes over work the model now requires (then the completion cascade exits it). *Corroborated independently by two reviewers.* [03 §7](03-cmmn-execution-semantics.md) also omits this clause (doc gap, §7 below).

### M2. Table 8.12 completion never re-evaluated when a child suspends
`StageBehavior.cs:589` gates the completion evaluation on `@event.Destination.IsTerminal()`; the comment above it (claiming criteria "can only change their answer when a child reaches one of Table 8.12's OWN states") is false for the no-Active-children conjunct — `suspend` removes a child from the Active count without a terminal destination. Result: **autoComplete outcomes are transition-order-dependent.** Required task completes then worker suspends the non-required sibling → stage sits Active forever; same events in the other order → stage completes. Identical final child states, divergent outcomes. Internally inconsistent too: the manual-completion path happily completes over the same Suspended child. *Corroborated independently by two reviewers.* (Whichever answer is intended — Table 8.9's `complete`+Suspended `<impossible>` cell arguably argues for *not* completing — it must not depend on ordering.)

### M3. `ParentSuspended` journaling corrupts the durable pre-suspension state
`StageBehavior.cs:458-463` / `TaskBehavior.cs:136-141` raise `ParentSuspended` **before** the `CanFire(ParentSuspend)` check, and `PlanItemStore.Apply(ParentSuspended)` (`PlanItemStore.cs:137-141`) records `ParentSuspendState = PlanItemState` — the *current* state at apply time — even when the transition then doesn't fire. The in-memory machine's history stays correct, so live behavior is fine; the corruption surfaces after replay. Trigger: duplicate delivery of the parent's Suspend event (at-least-once streams — the same property the #161 guard exists for). Second delivery journals PSS=**Suspended**, overwriting PSS=Available; after a recycle, `ParentResume` fires a Suspended→Suspended self-transition instead of restoring Available — the child is **permanently stuck Suspended** (violates Table 8.8 `parent resume` and Figure 8.3's history pseudo-state), and the spurious self-transition publishes a `parentResume` transition event that downstream OnParts consume as genuine. Fix shape: raise the event only when the transition will fire, or record `transition.Source` in the event rather than reading current state at apply time.

### M4. Table 8.9 Note (1) unimplemented — Failed child reactivated under a Suspended parent runs inside a suspended container
Note (1) (printed p.117-119): a Failed child under a suspended parent, once reactivated, "should continue transition into Suspended state." `PlanItemStateMachine.cs:274-276` permits `Reactivate → Active` unconditionally; no behavior consults the parent's state. Also reachable: reactivating a Failed child under a *Completed* parent (a legal Table 8.12 completion) produces an Active-under-Completed configuration Table 8.9 marks `<impossible>`. *Corroborated independently by two reviewers.* (Spec verb is "should," but the resulting configurations have no Table 8.9 row.)

### M5. Public `Trigger` surface accepts engine-internal transitions
`ICaseGrain.Trigger` / `IPlanItemGrain.Trigger` → `BaseBehavior.Trigger` (`BaseBehavior.cs:82`) fire any transition the machine permits; only Complete is gated. The wire enum (`Wayfinder.Contracts/V1/PlanItemTransition.cs`) exposes `ParentSuspend`, `ParentResume`, `ParentTerminate`, `Exit`, `Fault`, `Create`, `Start`, `Enable`, `Occur` — transitions Tables 8.6/8.8/8.11 reserve for the engine. Example: `Trigger(ParentSuspend)` on an Available task suspends it *without* the `ParentSuspended` journal event (only raised in `HandleParentTransitioned`), so PSS=null; after a recycle the only exits are `Resume`→Active (skipping entry criteria and the ManualActivationRule — a task that was never started becomes Active) or Exit. `Trigger(Fault)` fabricates a "software failure." Fix: allowlist the case-worker transitions (disable/manual-start/suspend/resume/re-enable/re-activate/terminate/complete/close, +occur for UserEventListener) at the Trigger surface.

### M6. The "suspension loses work" class — three unfixed instances of the #178 shape
Suspension elsewhere in this engine is preserve-and-restore (that was #178's fix for repetition spawns). Three paths still discard:
- **(a) Entry-criterion satisfaction delivered to a Suspended task/stage strands the original and spawns a phantom.** The branch ladder in `HandleSentrySatisfied` (`TaskBehavior.cs:239-275`, same shape `StageBehavior.cs:353-389`) sends any non-Available state into the `else if (OnPartOccurred && EvaluateRepetitionRule())` arm, which **unsubscribes the original's entry criteria and marks it `Repeated`** — the original resumes to Available permanently unable to activate (the `Repeated` early-return guarantees it), while a sibling repetition spawns via the parent's buffer. Reachable in-engine: CaseFileItems are case-global and not suspended, so a CFI update during stage suspension fires the sentry. The same ladder also makes a Disabled instance eligible for the repetition branch (no such Table 8.8 semantics). *Corroborated independently by two reviewers.*
- **(b) A Suspended milestone permanently misses its achieving sentry.** `MilestoneBehavior.cs:96-118`: satisfaction during Suspended journals `EntryCriterionSatisfied` and fires nothing; nothing on Resume re-checks; the sentry won't re-publish (occurrence-memory latching, M8/M9). The milestone can never be achieved by that event.
- **(c) A timer tick during suspension is silently dropped.** `TimerEventListenerBehavior.HandleTimerTickedEvent` (`TimerEventListenerBehavior.cs:204-219`) handles only Available and Completed; suspend neither pauses nor cancels the Quartz job, and no Resume hook reschedules or back-fills an elapsed fire time. Suspension across a timer's sole fire time is termination-in-effect.

### M7. Timer semantics — three defects beyond C2/C3
- **(a) Duration-only expressions fire immediately.** `ConfigureTrigger` (`TimerEventSchedulerGrain.cs:71-94`) sets `StartAt(timerStart ?? schedule.Start ?? DateTime.UtcNow)` and puts the duration only into `WithInterval(...)`; a non-repeating Quartz simple trigger fires once **at StartAt** and never consults the interval. `PT15M` occurs at creation, not 15 minutes later. Only R-schedules with explicit starts are integration-tested; the plain-duration case is untested and wrong.
- **(b) #182 sharpened: the ISO-8601 repeat bound never decrements.** Every post-Completed tick publishes a repetition request (`TimerEventListenerBehavior.cs:210-218`), and each spawned instance re-evaluates the timerExpression and schedules the **full** `R<n>` schedule afresh (`:81-138`) — `R2/PT1H` chains until the #67 ceiling faults the parent, and the Completed predecessor's Quartz job is never canceled (only Terminated cancels, `:152-154`), leaking jobs. Spec note: Table 5.55 says **EventListeners cannot carry a RepetitionRule** — timer repetition is legitimately driven by the R-notation, so the correct fix bounds by `Iso8601.Repetitions`, not by a rule the element cannot have. ([03](03-cmmn-execution-semantics.md)'s "timer repetition ignoring the rule" framing should be read in that light.)
- **(c) ISO-8601 parsing rejects conforming values.** `Iso8601.cs:78-119` pipes durations through NodaTime `Period.ToDuration()`, which **throws for any months/years component** (`P1M`, `P1Y`, `P1Y2M3DT4H` — verified empirically); and `InstantPattern.ExtendedIso` (`:86-103,123-126`) rejects offset (`…+02:00`) and local date-times — only UTC-'Z' parses. All are conforming per Table 5.19. The failure is a caught log-and-Failure: the timer silently never fires. None of these cases are in `Iso8601Tests`.

### M8. Sentry occurrence-memory swallows genuinely new occurrences
`SentryStore.IsRedelivery` (`SentryStore.cs:64-66`) treats `recorded == occurrenceToken` as redelivery, with the token = the source's `SourceInstanceId` — true for real redeliveries, but equally true for a *distinct later occurrence by the same instance*. Any OnPart watching a repeatable transition (suspend/resume/parentSuspend/parentResume/disable/reenable/fault/reactivate) fires **at most once per source instance, ever**. The store's own remarks argue only the one-shot case. Fix shape: add a per-occurrence component (e.g. source journal version) to the token.

### M9. CaseFileItemOnPart without IfPart latches after the first occurrence
`SentryGrain.cs:190-204` passes `occurrenceToken: null`; `IsRedelivery` treats any null-token occurrence as a redelivery once one is recorded, and the only clear path is `OnPartNotRearmed` (IfPart-FALSE/fault only). A sentry with a CFI OnPart and **no IfPart** satisfies exactly once per case — CFI-driven repetition (§8.6.4: "every time an entry criterion with an OnPart is satisfied"; Table 8.2's update/replace are repeatable Available→Available transitions) is impossible. With an IfPart, the path only works because **C4's bug masks it** — the two defects currently cancel on that path, so fixing C4 alone will surface M9.

### M10. IfPart never re-evaluated on CaseFileItem changes; `OnPartNotRearmed` can make a sentry permanently unsatisfiable — *plausible* (permanence half confirmed)
The IfPart is evaluated only in the turn an OnPart completes the AND-join (`SentryGrain.cs:303-362`); on FALSE, the triggering occurrence is **deleted**. §8.5 defines OnPart satisfaction as a past event occurrence — nothing un-satisfies it — and says sentries are evaluated "when events arrive to the system" (a CFI update is such an event). With a one-shot source: B completes while `amount==50` → occurrence discarded; later `amount=200` → per §8.5 the sentry should now be satisfiable, but no CFI subscription exists on OnPart-bearing sentries and the occurrence cannot recur → dead forever. Marked plausible only because §8.5's re-evaluation scope is arguable; the permanent-unsatisfiability half is confirmed code behavior.

### M11. Cross-stage OnParts (#176) — status confirmed, blast radius wider than the filter
The scope filter is unchanged (`SentryGrain.cs:172`: `if (!@event.SourceScope.StartsWith(_scope)) return;`). Beyond it: (a) Table 8.7's second becoming-Available bullet (bottom-up activation with recursive ancestor activation) is **unreachable end-to-end** — child PlanItem grains are only defined in `CreateChild`, i.e. after the parent is already Active, so there is no subscriber to activate bottom-up even with the filter removed; (b) the ancestor-activation ladder in `HandleChildTransitioned` (`StageBehavior.cs:553-580`) is consequently dead code on first-instantiation paths; (c) `CmmnCapabilityLint` has no rule for cross-stage sourceRefs — such models deploy clean and silently never fire. **Hazard for whoever fixes #176:** the bottom-up block fires Start on the parent, whose `HandleEnterActiveFromStart` unconditionally creates one instance of *every* child PlanItem with no check against existing `StageStore.Children` — including a duplicate of the very child whose Available event triggered activation. Also: the ladder auto-fires `re-enable`/`manual start` from Disabled, silently overriding an explicit human Disable (Table 8.8 reserves those for case workers).

### M12. First-instance sentry race: satisfaction can be published before the dependent subscribes — *plausible*
`HandleEnterActiveFromStart` creates children via unordered `Task.WhenAll`; a non-blocking sibling can run Create→…→Completed inside the fan-out, its transition reaching the already-subscribed SentryGrain and publishing satisfaction before the dependent sibling's `SubscribeToCriteria(StreamFlags.Create)` has run. The satisfaction is never redelivered (`SentryStore.Satisfied` latch; nothing replays to late subscribers — the same gap as #177, which remains unfixed for repetition instances: `CreateChild` at `StageBehavior.cs:1736-1762` only fires Create, never the §8.6.4-mandated immediate Available→Active/Enabled move). Timing-dependent; memory-stream agent latency usually masks it; nothing orders it.

---

## 4. Major findings — distributed architecture

### D1. No outbox: journal confirm and stream publish are non-atomic
`BaseBehavior.HandleTransitioned` confirms the journal, *then* publishes (`BaseBehavior.cs:149-178`); JournaledGrain replay is a pure fold — entry actions and publishes do not re-run. A crash between confirm and publish yields a durably-recorded transition no observer ever hears about. **Sharpest instance:** `TryRepeatOnCompleteOrTerminate` runs as an entry action *after* the terminal transition is confirmed (`BaseBehavior.cs:116-135`) — a crash in that window leaves the child durably terminal with no `Repeated` and no request published: the repetition silently never happens and the stage completes over it. **This is the #198 defect resurrected through a crash channel** — the fix's same-turn invariant holds against interleaving (verified: non-reentrant grain, `GetSnapshot` queues behind the turn) but not against a crash *inside* the turn, because the transition and the determination are two separate blob writes. The mirror windows (publish-then-crash-before-confirm) were checked and are benign or covered by the #161 guard. Requires an outbox/republish-on-replay design; a stream-provider swap does not fix it.

### D2. Parent crash mid-fan-out produces durable orphan children
`CreateChild` (`StageBehavior.cs:1736-1780`): the child confirms its own `Defined`/`Create` durably; the parent's `ChildCreated` sits in TentativeState until the batch confirm at the end of `HandleEnterActiveFromStart` (`StageBehavior.cs:239-244`). A parent crash mid-fan-out leaves children that durably exist and run while `StageStore.Children` has no record — and `GetChildInstances`, all Table 8.12 evaluation, the #198 gate, and `CaseViewProjector` key off `StageStore.Children`, so the orphans are invisible everywhere, with no reconciliation path (`Create` can't re-fire from Active). Same shape for `SpawnRepetitionOrRefuseCeiling` (`StageBehavior.cs:1696-1731`), where duplicate-on-retry was correctly chosen over lose-on-retry — but the duplicate then escapes the parent's books if the trailing confirm never runs.

### D3. Distributed wait cycle: CaseGrain ↔ CaseFileItemGrain — *plausible (both edges confirmed)*
Edge A: cascade turns evaluate rules/IfParts with a `contextRef` → `ExpressionGrain` (StatelessWorker) → `CaseFileItemGrain.GetSnapshot` (`ExpressionGrain.cs:121`). Edge B: every CFI mutation calls `EnsureCaseNotClosed` → `CaseGrain` (`CaseFileItemGrain.cs:290-304`). Concurrent API-driven case-file update + case trigger whose cascade evaluates a rule over the same item = a cycle with different call-chain origins that call-chain reentrancy cannot break; both sides hang until the 30s Orleans response timeout aborts one, failing both and potentially leaving D2-shape partial state. Probability scales with rule-evaluation frequency under ordinary concurrent load.

### D4. Unbounded per-grain growth and subscription fan-out over case lifetime
`StageBehaviorStore.Children`, `_repetitionSourceInstanceIds`, `_settledRepetitionSourceInstanceIds` grow monotonically (acknowledged at `StageBehaviorStore.cs:33-45,75-77`); `HandleEnterTerminal` unsubscribes only exit criteria (deliberate, per #178), so every terminal child instance — one per repetition — retains live entry-criteria/parent-transition/child-stream subscriptions. Every publish on a parent-definition stream fans out to and re-activates every child instance ever created; cost grows linearly with repetition count on top of journal-replay cost. The #67 ceiling bounds it; the default is generous.

### D5. API views are torn by construction; no concurrency tokens — note, by design
`CaseViewProjector.BuildAsync` walks the tree with sequential unversioned `GetSnapshot` calls, so a returned view can show Table-8.9-impossible combinations mid-cascade; `Trigger` returns before stream propagation settles; no ETag/version is exposed. Acceptable as eventual consistency, but nothing in the API contract says so — needs an explicit statement.

---

## 5. Findings — interchange, expressions, model handling

### I1. Legal `<planFragment>` (and generic `<task>`) silently dropped at import — major
`Stage.PlanItemDefinitions`' element map (`Wayfinder.Grains.Interfaces/Model/Spec.CMMN.MODEL.cs:844-852`) omits them; XmlSerializer ignores unknown elements (no `UnknownElement` hook in `CmmnXmlSerializer.cs`). PlanFragment is a concrete PlanItemDefinition (§5.4.4) that discretionary planning explicitly plans (§8.7). A model containing one imports "successfully," the fragment vanishes, the discretionaryItem's definitionRef dangles — and **the capability lint cannot warn because the construct no longer exists when it walks the graph**. This is the dangerous category: silent semantic loss upstream of the honesty gate.

### I2. Spec-default XPath documents silently run as JavaScript — major
Table 5.3: absent `expressionLanguage` defaults to XPath; Table 5.33: an Expression without `language` inherits it. `CmmnCapabilityLint.IsNonJint` (`CmmnCapabilityLint.cs:274-275`) returns false for null/blank, so a document declaring nothing — i.e. XPath by spec, the common case for third-party models — imports with **zero findings** and every expression runs as Jint. Explicitly-declared non-Jint languages are flagged; the spec-default case is not. Documented as deliberate (`Definitions.cs:113-122`) but it defeats the lint's stated purpose.

### I3. Non-spec `parentTerminate` leaked into the interchange enum — minor
`Spec.CMMN.MODEL.cs:2760-2763` hand-appends `ParentTerminate` to the XSD-generated `PlanItemTransition`. Table 5.31 enumerates exactly 17 values without it (it exists only in the §8.4.3 lifecycle, deliberately not exposed as an OnPart standardEvent). Import accepts schema-invalid documents; export can emit documents other engines reject. Internal lifecycle use is fine; the interchange surface leak is not.

### I4. Absent `contextRef` binds nothing instead of the caseFileModel — minor
Tables 5.32/5.38/5.52/5.53: "If not specified, evaluation starts at the CaseFile object referenced by the Case as its caseFileModel." `ExpressionGrain.BuildExecutable` (`ExpressionGrain.cs:116-119`) returns a bare executor with no bindings — the condition errors and the sentry never satisfies. A deviation, not a simplification; not lint-flagged. Related note: the expression context binds exactly **one** CFI (the contextRef); a condition over two CaseFileItems is inexpressible; Jint truthy coercion applies; evaluation reads the *current* CFI snapshot at delivery time, not the value at transition time.

### I5. Parameter/data-flow and role surface silently ignored and unlinted — minor
`Case.Inputs/Outputs`, `BaseTask.Inputs/Outputs`, `CaseParameter.BindingRef/BindingRefinement`, all `ParameterMapping`s: imported, consumed nowhere (verified by grep), unlinted — Table 5.42 gives bindingRef normative semantics for HumanTasks too, not just the process/case/decision tasks the lint covers. Likewise `HumanTask.PerformerRef` and `TableItem.AuthorizedRoleRefs` (§8.6.5 requires the role filter during planning; `PlanningTableGrain.GetPlannableItems` evaluates only applicability). Contrast: `UserEventListener.AuthorizedRoleRefs` *is* enforced — though only for the `UserCompletable` flag; `PlanItemGrain.Trigger` fires `Occur` for any caller (authorization gap at the transition surface).

### I6. Production deploy does not gate on `Unsupported` lint findings — minor
`DeployDefinitionCommandHandler` (`Wayfinder.Application/Cases/DeployDefinitionCommandHandler.cs:46-49`) converts all findings to warnings and deploys unconditionally; the integration ConformanceHarness throws on `HasUnsupported` but production does not. Per the lint's own text, a blocking ProcessTask "sits in Active forever."

### I7. Smaller confirmed items
- `Stage.PlanItemDefinitionsNested` (`Stage.cs:19-34`) yields every nested stage twice → duplicated lint findings (noise, not semantics).
- Statically-dead standardEvent/source-kind combinations pass silently (e.g. an OnPart watching a Milestone for `complete` can never occur — Table 5.30/5.21 constrain standardEvent to the source's lifecycle); fair lint territory, not §9.1-protected.
- Structural MUSTs unvalidated anywhere: Sentry needs IfPart or ≥1 OnPart (Table 5.26); EventListener planItems must not have entryCriteria; EventListener/Milestone must not have exitCriteria; non-blocking Task must not have exitCriteria/outputs/planningTable (Tables 5.24/5.39/5.43); exitCriterionRef containment; exitCriteria only on the casePlanModel stage (Table 5.34). Import leniency is §9.1-sanctioned; the gap is the absence of any strict mode before Define.
- CMMNDI silently discarded per file (documented scope cut; no per-import warning).
- OnPart match predicate operator-precedence defect (`SentryGrain.cs:174-178`): parses as `blank(SourceRef) || (match && …)` — a blank SourceRef matches **any** source with the right standardEvent and bypasses the exitCriterionRef filter. Latent while models populate sourceRef; one malformed import from live. Additionally `SingleOrDefault` (`:174-178`, `:192-195`) throws inside the stream handler if two OnParts legitimately match one event (poison retry-then-drop instead of satisfying both). *Corroborated independently by two reviewers.*
- `EventListenerBehavior.cs:19-30`'s `UserCompletable = CanFire(Complete)` is dead logic — `Complete` is never configured on the Milestone/EventListener machine; probably intended `CanFire(Occur)`.
- Standalone-IfPart sentry satisfies at most once per case (`SentryGrain.cs:216`); defensible for entry criteria, wrong as the exit criterion of a repeating item (instance 2+ can never exit).
- Case reactivate-from-Completed is foreclosed: the completion cascade exits Available/Enabled/Suspended children at the case root, so a reactivated case (Table 8.4's semi-terminal Completed) returns Active with every prior child Terminated; only newly-planned discretionary items can work. Defensible under §8.4.1, but needs an explicit decision record.

---

## 6. The honest conformance subset

What a model author can rely on today (verified): single-file CMMN 1.1 documents with a casePlanModel containing nested Stages, HumanTasks (blocking and non-blocking), Milestones, User/Timer event listeners, sentries (planItem + caseFileItem OnParts incl. `exitCriterionRef`, IfParts), plan-item-control rules with correct absence defaults, flat CaseFileItems, `autoComplete`, and discretionary items with applicability rules — with every expression evaluated as sandboxed Jint JavaScript regardless of declared language.

Not faithfully executed (and only partly admitted by the lint): Process/Case/DecisionTasks; any non-Jint expression language *including the spec's default XPath*; CaseFileItem hierarchies; the entire parameter/data-flow surface; roles/authorization (except UserEventListener's flag); `<planFragment>`; cross-file imports/QName resolution; CMMNDI; cross-stage sentries (#176); repetition-instance activation (#177); month/year durations and non-UTC timestamps in timers.

---

## 7. Doc drift (docs vs. spec vs. code)

| Doc claim | Reality |
|---|---|
| [03](03-cmmn-execution-semantics.md) conformance table: #178, #179 "Confirmed, unfixed" | Both are **fixed on this branch** (#178: refuse/buffer at `StageBehavior.cs:1155-1282`, commit 721eef5; #179: complete-cascade at `StageBehavior.cs:489-527` + `TaskBehavior`, with integration tests) |
| [03](03-cmmn-execution-semantics.md) line 9: "printed ≈ PDF − 16" | **− 18** (verified thrice; printed 119 = PDF 137) |
| [03](03-cmmn-execution-semantics.md) §8: "a durable stream provider would close that gap" | Understated — also needs durable PubSubStore, a re-subscription strategy for Resume-only sites (C1), and an outbox (D1) |
| [03](03-cmmn-execution-semantics.md) §2: "no repetition trigger can legitimately originate inside a genuinely Suspended Stage" | Overstated — the entry-criterion path can raise `Repeated` on Enabled/Suspended children (handled correctly by the gate; the rationale is wrong) |
| [03](03-cmmn-execution-semantics.md) §7: RequiredRule evaluated "on instantiation into Available" | Omits Table 8.8's mandatory re-evaluation on `enable`/`re-enable` (M1) |
| [03](03-cmmn-execution-semantics.md) §4 | Omits Table 8.9's Milestone/EventListener columns (they legitimately survive a completed parent; the code gets this right) |
| [08](08-orleans-provider-evaluation.md) §2: streams are "fire-and-forget (`FireAndForgetDelivery = true`)" | Wrong — `AddMemoryStreams` is a pulling-agent, at-least-once-while-alive provider (why #161 exists); contradicts both 03 and observed behavior |
| [08](08-orleans-provider-evaluation.md) cites `Flow.Silo` paths/line numbers | Stale — files don't exist in this tree |

Everything else checked in [03](03-cmmn-execution-semantics.md) (§8.5/§8.6 paraphrases, Tables 8.7–8.12, defaults table, repetition triggers) matches the normative PDF text.

---

## 8. Architecture assessment and alternatives

**Verdict: Orleans is the right substrate; grain-per-plan-item is the wrong granularity, and it is the root cause of the dominant bug class.**

Evidence, all in-repo: CMMN's sentry/completion semantics are a case-scoped fixpoint evaluation ("a single event may satisfy multiple sentries"; completion needs all children's states simultaneously), distributed here across per-element grains wired by streams that are unordered across streams and volatile in both environments. The reconstruction machinery is the codebase's largest and most-churned component (`StageBehavior.cs` at 1,782 lines of buffer/hold/replay logic), the last five commits on this branch are all race-class fixes, and the test-flake surface is structural — `JournalPolling` exists because "must NOT fire" assertions have no quiescence point over fire-and-forget delivery; the #153 mitigation treated a symptom. The per-element journals also mean there is **no total order across a case's audit trail**, weakening the project's headline claim.

**Industry comparison:** Flowable — the only mainstream surviving CMMN engine — runs a command → agenda → sequential-operations-until-drained loop, one DB transaction per command: ordering and atomicity of the sentry fixpoint by construction. Camunda dropped CMMN for **market** reasons, not architectural ones (thin adoption), which is strategically relevant but not an indictment of the engine model. .NET alternatives (Elsa 3, Dapr Workflow/Durable Task) are worse hosts for CMMN's event-condition-action semantics — deterministic-replay workflow-as-code fights sentry semantics on every step, and none provide plan-item semantics; adopting one means rebuilding the CMMN layer on weaker distribution primitives.

**Stack inventory verdict:** no dependency swaps warranted. Orleans 10.2.1 (pinned uniformly), Stateless 5.20.1, Jint 4.11.0 (sandboxed), NodaTime, Serilog, the hand-rolled `ISender` seam (zero deps in Contracts), the test stack (xUnit/Testcontainers-Azurite/pinned FluentAssertions 7) — all well-chosen and unusually well-verified. Quartz is the exception (see R2). Test:product ratio 1.46:1 is healthy. The behavior layer (`IBehaviorHost`), Stateless machines, event model, conformance corpus (35 real `.cmmn` scenarios with spec-citation attributes), and Application/Api layers all survive a regranulation intact.

### Recommendations (ranked)

- **R1 — KEEP: Orleans as substrate.** Hosting, placement, JournaledGrain persistence, reminders, compound-key multi-tenancy, TestCluster. Effort: none.
- **R2 — CHANGE: collapse intra-case granularity.** One `JournaledGrain` per **case instance**; plan items, sentries, and case-file items as in-memory objects inside it; a Flowable-style synchronous agenda/criteria-evaluation loop inside the grain turn; Orleans reminders (targeting the case grain) replace Quartz; `ExpressionGrain` optionally stays a StatelessWorker so heavy Jint work doesn't block the case turn. Eliminates: the unordered-stream race class (#177/#181/#186/#198 lineage), the stream/PubSub durability gap (C1 — no intra-case streams to lose), the timer-loss and context classes (C2, C3 — reminders are durable and in-context), the duplicate-sentry leak (C5 — sentries stop being grains), the outbox problem (D1 — one journal, one write), orphan children (D2 — one turn), the wait cycle (D3 — one mailbox), most of StageBehavior's buffering, and the structural test flakes (a grain call returns when the fixpoint completes; `JournalPolling` retires). Gives a totally-ordered per-case journal. Cost: the biggest refactor available (weeks); serializes each case through one mailbox — an acceptable trade since per-case throughput is human/agent-paced, per-case serialization is what the current design reconstructs by hand anyway, and cross-case parallelism (the real concurrency) is untouched. No production deployment or persisted data to migrate. **Accepted — see §9.**
- **R3 — CHANGE: drop Quartz for Orleans reminders.** Subsumed by R2 (reminder lands on the case grain, routes internally). Removes RAMJobStore volatility, the scheduler-grain SPOF, and the #197 instanceName class.
- **R4 — fallback only if R2 were rejected: durable streams + durable PubSubStore + re-subscription + outbox.** Fixes lost-events, not reordering; adds per-event queue latency to every intra-case hop. A patch, not a cure.
- **R5 — KEEP: test architecture shape.** Conformance corpus drives the public surface and carries over unchanged. Post-R2, delete the polling/scaled-window helpers.

---

## 9. Decision record

**D-2026-08-01 — Granularity redesign first.** Decision: adopt **R2** (one journaled grain per case instance with an internal synchronous evaluation loop; Orleans reminders replace Quartz per R3) **before** addressing the individual findings above. Rationale: the majority of critical/major findings are structural consequences of the current topology and would be fixed-then-discarded; the redesign closes them by construction.

### Finding disposition under D-2026-08-01

**Closed structurally by the redesign** (do not fix on the old topology): C1, C2, C3, C5, M3 (no cascade transport), M8/M9/M12-transport-half (no redelivery concept intra-case), D1, D2, D3, D4, plus the #153 flake class and #161/#177/#181-transport lineage.

**Must be designed INTO the new evaluation loop** (the redesign does not fix these by itself — they are semantic, and carrying them over unexamined re-creates them in-process):
- M1 (RequiredRule re-eval on enable/re-enable — Table 8.8 MUST)
- M2 (completion re-evaluation on child suspend — make the loop re-evaluate on *every* state change, which the agenda model gives naturally)
- M4 (Table 8.9 Note 1 — reactivate lands Suspended under a suspended parent)
- M5 (Trigger allowlist — case-worker transitions only)
- M6 a-c (suspension preserves pending satisfactions/ticks — becomes trivial in-process, but must be specified)
- M7 a-c (timer duration/first-fire semantics, R-bound decrement, ISO-8601 parsing — Iso8601.cs carries over as-is and stays wrong unless fixed)
- C4's latch logic and M10 (sentry satisfaction algebra and occurrence memory — the *rules* need a correct specification regardless of where they run)
- M11 (#176 cross-stage OnParts + bottom-up activation — trivially expressible in-process; specify the duplicate-instance guard)
- The sentry match predicate precedence/multi-match defect (I7).

**Orthogonal — unaffected by the redesign, schedule independently:** all interchange findings (I1–I7), I5's authorization gaps, D5 (API consistency contract), doc drift (§7), and the conformance-subset honesty items (§6).

---

## 10. What is genuinely good

Recorded so the redesign preserves it: near cell-perfect lifecycle tables against the PDF; correct rule defaults on all paths including error paths (the #158 class is closed, including first-evaluation-discard for repetition); the Table 8.9 per-type cascade asymmetries honored in both directions; the #198 blocking/clearing-signal design (sound against interleaving, and the pattern worth keeping where cross-grain edges remain); journal-confirm discipline and source-owned idempotency keys; the XmlSerializer collection-shadow mechanism with reflection coverage test; DTD/XXE-hardened import; sandboxed Jint with honest error paths; CaseFileItem lifecycle fidelity (all eight Table 8.2 transitions, Available-only guards, Closed lockdown); sentryRef/exitCriterionRef OnParts implemented end-to-end; and code comments that state their ordering assumptions and residual windows honestly enough to be verifiable.
