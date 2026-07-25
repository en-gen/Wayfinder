# CMMN 1.1 Conformance Suite — Coverage Map (ADO #21)

The internal TCK: every scenario is a real `.cmmn` file (`Conformance/Samples/*.cmmn`), imported
through `CmmnXmlSerializer.Import` → `CmmnCapabilityLint` → `ToDeployableCase`, deployed via
`ICaseDefinitionGrain.Define` → `ICaseGrain.Create`, and driven through the public grain surface
only. Spec references are to `formal-16-12-01.pdf` (OMG CMMN 1.1; PDF page = spec page + 18).

**Honesty rule.** Every spec table/row in scope appears below — rows the engine cannot pass are
`KnownGap` with the owning work item (or the #21 report finding id, for gaps this suite
discovered that have no work item yet), never omitted. A `KnownGap` row has an executable,
`[Fact(Skip = ...)]`-quarantined scenario in `KnownGapScenarios.cs` whose body asserts the
spec-mandated behavior and whose skip reason records the behavior observed on `develop@f23a74b`
(2026-07-10). Unskip when the cited work item lands; a passing unskipped scenario flips its row
to `Pinned`.

**Status legend.**
- `Pinned` — a green scenario asserts the row against the running engine.
- `KnownGap:<ref>` — quarantined scenario; engine behavior deviates today; `<ref>` owns the fix.
- `NotApplicable` — the row cannot be exercised through what the engine implements today (reason
  given); no scenario pretends otherwise.

Suite verified at authoring: **33 scenarios — 23 executed green, 10 quarantined (skipped), 0 failing**
(`develop@f23a74b`, 2026-07-10).

**Un-quarantine pass (ADO #21, work item #19's PR !26 landed).** Every quarantined scenario
re-probed by unskipping and running against `develop@cbd66d7` (2026-07-10, branch
`test/21_unquarantine-post-19`), which contains both the conformance suite (PR !25) and the
rules-completion fixes (PR !26, work item #19: D4, D7, D8 remainder, Bug #62). Two scenarios
passed outright and are lifted to `Pinned` below (D7's repeat-on-complete/terminate; Bug #62's
child-repetition stream delivery). Two more of #19's candidates were re-probed and found STILL
blocked, each with an updated, more precise finding recorded in `KnownGapScenarios.cs` and
re-quarantined rather than lifted: D4's `Trigger(Complete)` enforcement gate is fixed but its
`UserCompletable` observability flag still uses the pre-fix conflated condition
(`StageBehavior.HandleChildTransitioned`); and the case-reactivate child-release scenario's D8
half is resolved but it remains blocked purely by FINDING-1/#63 (parent-to-child propagation),
unaffected by !26. The six scenarios owned by FINDING-1/#63, FINDING-2/#64, and FINDING-3/#65
were re-verified unchanged - identical failure signatures to the original authoring run.

Suite re-verified post-!26: **33 scenarios — 25 executed green, 8 quarantined (skipped), 0 failing**
(`develop@cbd66d7`, 2026-07-10, `test/21_unquarantine-post-19`).

**FINDING-2/#64 fix (branch `bugfix/64_autostart-activation-context`, 2026-07-11).**
`KnownGapScenarios.StageAutoStart__…` un-quarantined and re-probed against
`develop@0eae663`: confirmed RED with the exact predicted stack
(`InvalidOperationException: Activation access violation. A non-activation thread attempted to
access activation services.` from `Host.GrainFactory` in `StageBehavior.CreateChild`, via
`Stateless.StateMachine.InternalFireQueuedAsync` → `HandleEnterActiveFromStart`). Root cause:
`PlanItemStateMachine` never set Stateless's own `RetainSynchronizationContext` flag, so every
internal await Stateless takes between the reentrant `FireAsync(Start)` (queued mid-`Create` by
`StageBehavior`/`TaskBehavior.EnableOrStart`) and later draining that queue used
`ConfigureAwait(false)` at its default `false` value — losing Orleans' `TaskScheduler.Current`
capture the moment any real async work (`Host.ConfirmEvents`, stream subscriptions, the
`ManualActivationRule` `IExpressionGrain` call) came between them. Fixed by setting
`PlanItemStateMachine.RetainSynchronizationContext = true` in its constructor (`StateMachine.cs`)
— opts every one of those internal continuations back into ordinary captured-context `await`
semantics, matching the rest of this grain's code, with no change to when/what fires. Re-run
green; full suite (`Flow.Grains.Tests` 286/286, `Flow.Grains.Tests.Integration` 125 passed/4
skipped/0 failed) shows no regressions, including the #63 cascade and #65 nested-declaration
scenarios.

**D4/#68 fix (branch `bugfix/68_usercompletable-table812`, 2026-07-11).**
`KnownGapScenarios.StageCompletion__…ManualCompletionBecomesAvailable` un-quarantined and
re-probed against `develop@cc67626`: confirmed RED exactly as the skip reason predicted —
`UserCompletable` stayed `false` after the required child completed while the non-required child
stayed `Active`. Root cause confirmed at `StageBehavior.HandleChildTransitioned`'s flag-raise
condition: it never checked `PlanItemDefinition.AutoComplete` at all (so an autoComplete=TRUE
Stage — which has no Manual Completion branch in Table 8.12, it just auto-completes with no human
involvement — got latched "user-completable" anyway) *and* it required
`childSnapshots.All(state != Active)` before raising, which is the autoComplete=TRUE column's
condition, not autoComplete=FALSE's Manual Completion OR-branch (that branch requires only that
*required* children be terminal — the same `!26`/#19 already established for the
`Trigger(Complete)` enforcement gate in `ManualCompletionCriteriaSatisfied`, just never carried
over to this flag). Fixed by gating the raise on `!PlanItemDefinition.AutoComplete` and dropping
the no-Active-children conjunct, so `UserCompletable` now mirrors
`ManualCompletionCriteriaSatisfied`'s own autoComplete=FALSE arm. Re-run green; full suite
(`Flow.Grains.Tests` 287/287, `Flow.Grains.Tests.Integration` conformance 33/33, full integration
126 passed/3 skipped [pre-existing Azurite-emulator skips, unrelated]/0 failed) shows no
regressions — the #63/#64/#65 scenarios and the `AutocompleteChildrenTerminal` unit test (updated
to assert `UserCompletable` now stays unlatched for autoComplete=TRUE, per this fix) all stay
green. This was the last quarantined conformance scenario: the suite is now 33/33 green, 0
skipped.

**FINDING-1/#63 fix (branch `bugfix/63_definition-id-parent-subscription`, 2026-07-11).** Keyed
`BaseBehavior.Activate`'s parent-transition subscription by the parent's DEFINITION id (matching
`CmmnElementGrain.PublishEvent`'s publish key) instead of its instance id. Every FINDING-1-tagged
scenario in `KnownGapScenarios.cs` — `CaseSuspend__…SuspensionPropagatesToTask`,
`CaseTerminate__…TerminationPropagatesToMilestone`, `CaseReactivate__…ChildrenReturnToPriorState`,
`StageSuspend__…TaskFollowsByPropagationOnly`, `StageExit__…ExitCascadesTerminationToTask` — is
green: every downward Table 8.5/8.6/8.9 cascade this suite probes now delivers. FINDING-1 is
retired; #63 closed it directly (no separate work item was ever filed for the finding itself).

**#66 (CasePlanModel exit criteria) and #69 (Closed lockdown) fixes (2026-07-11/12).** #66 armed
and routed the CasePlanModel's own `<exitCriterion>` through Table 8.6's `terminate` transition —
`SentryScenarios.Sentry__Given_CasePlanModelExitCriterion__Then_CaseFileEventTerminatesCase`,
green, including the Table 8.9 cascade to a child Task. #69 added
`CaseFileItemGrain.EnsureCaseNotClosed`, guarding every CaseFileItem mutation
(Create/Update/Replace/AddChild/RemoveChild/AddReference/RemoveReference/Delete) once the owning
Case is Closed — covered by `CaseFileItemGrainTests`' `Update__Given_OwningCaseClosed__…`/
`Create__Given_OwningCaseClosed__…` unit tests, not a `.cmmn` conformance scenario. Table 8.5's
"case file becomes read-only" gap is closed.

**#87 (multiple entry criteria / OR-of-sentries) (2026-07-14).** Added
`Samples/Sentry_MultipleEntryCriteria.cmmn` and
`SentryScenarios.Sentry__Given_TwoEntryCriteria__Then_EitherAloneSatisfiesEntry`: a PlanItem with
two independent, single-OnPart entry criteria, satisfied by driving only the second criterion's
source. Table 8.11's "when ONE of the achieving Sentries (entry criteria) is satisfied" row —
previously untested — is now Pinned for the entry-criteria case (the equivalent exit-criteria-OR
shape remains unscenario-ized; see §8.5 below).

**Current state (develop HEAD, 2026-07-14).** 26 sample `.cmmn` files under `Conformance/Samples/`;
35 scenarios (`CaseFileScenarios` 3, `InstantiationScenarios` 2, `KnownGapScenarios` 10,
`LifecycleScenarios` 12, `SentryScenarios` 8) — **35 executed green, 0 quarantined/skipped, 0
failing**. `KnownGapScenarios.cs` keeps its name and the quarantine machinery described in the
honesty rule above for the next engine gap this suite finds, but every scenario inside it today is
an ordinary green `[Fact]`, not a quarantined `[Fact(Skip = ...)]` — that file's own header remarks
still narrate the ORIGINAL failure investigations (including FINDING-1) for historical context
only. This COVERAGE.md file is the current-status source of truth; where its per-row Status column
disagrees with prose elsewhere, the Status column wins.

## Engine findings discovered by this suite (details in the #21 report)

| Finding | One-line summary | Work item |
|---|---|---|
| FINDING-1 | Parent→child lifecycle propagation is stream-dead: children subscribe on the parent's *instance* id (`BaseBehavior.Activate`), grains publish on their *definition* id (`CmmnElementGrain.PublishEvent`) — every downward cascade of Tables 8.5/8.6/8.9 never delivers | #63 (fixed) |
| FINDING-2 | Auto-start Stages (FALSE ManualActivationRule) crash: the queued `Start` trigger runs `StageBehavior.HandleEnterActiveFromStart` on a non-activation thread — `Host.GrainFactory` throws "Activation access violation", no children instantiate | #64 (fixed) |
| FINDING-3 | Definitions declared inside a nested `<stage>` are unresolvable at runtime: the definition index keys definition-id paths, runtime scopes are instance-id paths — only casePlanModel-root declarations resolve | #65 (fixed) |

## §8.4.1 Case instance lifecycle (Tables 8.5, 8.6)

| Spec row | Scenario(s) | Status |
|---|---|---|
| Table 8.6 create (Ø → Active, skips Available) | `LifecycleScenarios.CaseCreate__…SkippingAvailable`; every `DeployAndCreate` asserts it implicitly | Pinned |
| Table 8.6 suspend (Active → Suspended), case's own transition | `LifecycleScenarios.CaseLifecycle__…SuspendAndReactivateWalkTable86` | Pinned |
| Table 8.6 suspend — downward propagation (Table 8.5 Suspended MUST) | `KnownGapScenarios.CaseSuspend__…SuspensionPropagatesToTask` | Pinned (#63) |
| Table 8.6 terminate (Active → Terminated), case's own transition | `LifecycleScenarios.CaseLifecycle__…TerminateAndReactivateWalkTable86` | Pinned |
| Table 8.6 terminate — downward propagation | `KnownGapScenarios.CaseTerminate__…TerminationPropagatesToMilestone` | Pinned (#63) |
| Table 8.6 terminate — via CasePlanModel's own exit criteria (8.4.1) | `SentryScenarios.Sentry__Given_CasePlanModelExitCriterion__…CaseFileEventTerminatesCase` (asserts both the Case's own terminate and the Table 8.9 cascade to a child Task) | Pinned (#66) |
| Table 8.6 complete (Active → Completed, via Table 8.12) | `LifecycleScenarios.TaskLifecycle__…CompleteCompletesCase` | Pinned |
| Table 8.6 fault (Active → Failed) | `LifecycleScenarios.CaseLifecycle__…FaultReachesFailedAndReactivateRecovers` | Pinned |
| Table 8.6 re-activate from Failed | `LifecycleScenarios.CaseLifecycle__…FaultReachesFailedAndReactivateRecovers` | Pinned |
| Table 8.6 re-activate from Terminated | `LifecycleScenarios.CaseLifecycle__…TerminateAndReactivateWalkTable86` | Pinned |
| Table 8.6 re-activate from Suspended (case's own transition) | `LifecycleScenarios.CaseLifecycle__…SuspendAndReactivateWalkTable86` | Pinned |
| Table 8.6 re-activate from Suspended — release of cascade-suspended children | `KnownGapScenarios.CaseReactivate__…ChildrenReturnToPriorState` | Pinned (#63 — D8's case-level remainder was resolved by !26; the remaining FINDING-1 block on reaching Suspended at all is fixed by #63) |
| Table 8.6 re-activate from Completed | — engine permits it (state machine); not scenario-pinned: reactivating a *completed* case is planning-driven (8.7) and planning-at-case-level has no runtime surface yet | NotApplicable (no planning surface) |
| Table 8.6 close (→ Closed) from Completed | `LifecycleScenarios.CaseLifecycle__…ClosesAndStaysClosedAgainstReactivation` | Pinned |
| Table 8.6 close from Terminated/Failed/Suspended | engine state machine permits all three (same `Permit(Close)` wiring pinned from Completed); only the Completed route is scenario-driven | Pinned (Completed route; other from-states covered by the same wiring) |
| Table 8.5 Closed is terminal (no re-activate out of Closed) | `LifecycleScenarios.CaseLifecycle__…StaysClosedAgainstReactivation` | Pinned (PR !26/#19: the rejection is now a loud `InvalidOperationException` from `CaseGrain.Trigger`, not a silent no-op; scenario updated to match) |
| Table 8.5 Closed — case file becomes read-only, no new planning | PR !26/#19 (`a13edd6`) added `CaseGrain.Trigger` throwing once Closed plus `CasePlanModelBehavior` reusing `HandleEnterTerminal` as a Closed entry action - "no new activity is allowed in the Case" (PlanItem transitions) is enforced. #69 (`CaseFileItemGrain.EnsureCaseNotClosed`) closed the remainder: every CaseFileItem mutation (Create/Update/Replace/AddChild/RemoveChild/AddReference/RemoveReference/Delete) now throws once the owning Case is Closed | Pinned (#69 — covered by `CaseFileItemGrainTests`' `Update__Given_OwningCaseClosed__…`/`Create__Given_OwningCaseClosed__…` unit tests; not a `.cmmn` conformance scenario) |

## §8.4.2 Stage and Task lifecycle (Tables 8.7, 8.8, 8.9)

| Spec row | Scenario(s) | Status |
|---|---|---|
| Table 8.8 create (Ø → Available; Repetition/Required rules evaluated) | `InstantiationScenarios.Instantiation__…TableMandatedStates`; `KnownGapScenarios.StageCompletion` precondition pins `Required=true` | Pinned |
| Table 8.8 enable (Available → Enabled, MAR TRUE) | `LifecycleScenarios.TaskLifecycle__…NoManualActivationRule…` (default TRUE per Table 5.51) | Pinned |
| Table 8.8 start (Available → Active, MAR FALSE) — Task | `LifecycleScenarios.TaskLifecycle__…ManualActivationRuleFalse…` | Pinned |
| Table 8.8 start — STAGE (auto-start + 8.7 instantiation) | `KnownGapScenarios.StageAutoStart__…` | Pinned (#64 - `PlanItemStateMachine.RetainSynchronizationContext`) |
| Table 8.8 manual start (Enabled → Active) — Task | `LifecycleScenarios.TaskLifecycle__…NoManualActivationRule…` | Pinned |
| Table 8.8 manual start — STAGE (+ 8.7 nested instantiation) | `LifecycleScenarios.StageLifecycle__…ManualStartInstantiatesChildren…` | Pinned |
| Table 8.8 disabled (Enabled → Disabled) | `LifecycleScenarios.TaskLifecycle__…DisableAndReenableRoundTrips` | Pinned |
| Table 8.8 re-enable (Disabled → Enabled) | `LifecycleScenarios.TaskLifecycle__…DisableAndReenableRoundTrips` | Pinned |
| Table 8.8 suspended (Active → Suspended, direct) | `LifecycleScenarios.StageLifecycle__…SuspendResumeWorks` (Stage); Task variant via parent-cascade only — see parent suspend row | Pinned (Stage direct) |
| Table 8.8 resume (Suspended → Active, direct) | `LifecycleScenarios.StageLifecycle__…SuspendResumeWorks` | Pinned |
| Table 8.8 parent suspend / parent resume (+ Table 8.9 note (2)) | `KnownGapScenarios.StageSuspend__…TaskFollowsByPropagationOnly` | Pinned (#63) |
| Table 8.8 fault (Active → Failed; MUST NOT propagate) | `LifecycleScenarios.TaskLifecycle__…FaultReactivateTerminateWalkTable88` (incl. parent-still-Active assert) | Pinned |
| Table 8.8 re-activated (Failed → Active) | `LifecycleScenarios.TaskLifecycle__…FaultReactivateTerminateWalkTable88` | Pinned |
| Table 8.8 complete (Active → Completed) — Task | `LifecycleScenarios.TaskLifecycle__…NoManualActivationRule…` | Pinned |
| Table 8.8 complete — RepetitionRule re-evaluation for no-entry-criteria items | `KnownGapScenarios.TaskRepetition__…CompletionSpawnsNewInstance` | Pinned (#19/D7, PR !26 `d991861`) |
| Table 8.8 terminate (Active → Terminated, Case worker) | `LifecycleScenarios.TaskLifecycle__…FaultReactivateTerminateWalkTable88` | Pinned |
| Table 8.8 exit — Task (exit criterion while Active) | `SentryScenarios.Sentry__Given_TaskExitCriterion__…` | Pinned |
| Table 8.8 exit — Stage (exit criterion while Active; D6 fix) | `SentryScenarios.Sentry__Given_StageExitCriterion__…` | Pinned |
| Table 8.9 exit/terminate propagation to children | `KnownGapScenarios.StageExit__…ExitCascadesTerminationToTask` | Pinned (#63) |
| Table 8.9 fault rows (children keep state on parent fault) | fault non-propagation pinned at the Case level (`TaskLifecycle__…FaultReactivate…`'s parent-still-Active assert); per-child state matrix not separately scenario-ized | Pinned (non-propagation observable) |
| Table 8.9 complete rows (`<impossible>` cells) | blocked behind Table 8.12's remaining gap (D4 remainder) — completing a stage with children in the listed states isn't reachable through the public surface today | KnownGap:#19 (D4 remainder) — via StageCompletion scenario (see below) |

## §8.4.3 EventListener and Milestone lifecycle (Tables 8.10, 8.11)

| Spec row | Scenario(s) | Status |
|---|---|---|
| Table 8.11 create (Ø → Available) | `LifecycleScenarios.MilestoneLifecycle__…WalkTable811`; `InstantiationScenarios` | Pinned |
| Table 8.11 suspend (Available → Suspended) | `LifecycleScenarios.MilestoneLifecycle__…WalkTable811` | Pinned |
| Table 8.11 resume (Suspended → Available) | `LifecycleScenarios.MilestoneLifecycle__…WalkTable811` | Pinned |
| Table 8.11 terminate (Available → Terminated) | `LifecycleScenarios.MilestoneLifecycle__…WalkTable811` | Pinned |
| Table 8.11 occur — Milestone (achieving sentry satisfied) | `SentryScenarios` (every milestone-completing scenario) | Pinned |
| Table 8.11 occur — EventListener (timer/user event) | timer start-trigger runtime is pinned by the pre-existing `CaseFileItemSentryIntegrationTests` timer scenario and `Scheduler` suites; UserEventListener occurrence needs role setup outside this suite's `.cmmn`-driven scope today | NotApplicable (covered elsewhere / role surface out of scope) |
| Table 8.11 parent terminate | `KnownGapScenarios.CaseTerminate__…PropagatesToMilestone` | Pinned (#63) |

## §8.5 Sentry

| Spec rule | Scenario(s) | Status |
|---|---|---|
| Satisfaction bullet 2: ALL OnParts, no IfPart (AND-join) | `SentryScenarios.Sentry__Given_TwoOnParts__…` | Pinned |
| Satisfaction bullet 1: OnParts + IfPart TRUE over CaseFile context | `SentryScenarios.Sentry__Given_IfPartOverCaseFileContext__…` (string condition); numeric variant pinned by pre-existing D1 flagship tests | Pinned |
| Satisfaction bullet 3: standalone IfPart, no OnParts (D3) | `SentryScenarios.Sentry__Given_StandaloneIfPart__…` | Pinned |
| "IfPart … evaluated for all CaseFileItem events" (case-wide) | `SentryScenarios.Sentry__Given_StandaloneIfPart__…` (unrelated-item event evaluated without spurious fire) | Pinned |
| Entry criteria ready while Available | every entry-criterion scenario (milestone waits in Available until satisfied) | Pinned |
| Exit criteria ready while Active (Task and Stage) | `SentryScenarios.Sentry__Given_TaskExitCriterion__…` / `…StageExitCriterion__…` | Pinned |
| Per-OnPart re-arm across distinct source occurrences (Figure 8.5 B/B′; D5 fix) | `SentryScenarios.Sentry__Given_RepeatableMilestone__…RearmsAcrossDistinctSourceInstances` | Pinned |
| PlanItemOnPart via `sourceRef`/`exitCriterionRef` (Table 5.30 exit mode) | `SentryScenarios.Sentry__Given_PlanItemOnPartWithExitCriterionRef__…` | Pinned (Bug #82 — D10 fix: `PlanItemStateMachine`'s parameterized Exit trigger threads the firing `ExitCriterion`'s own id through `BaseBehavior.HandleTransitioned` into `PlanItemTransitionedEvent.ExitCriterionRef`; `CmmnCapabilityLint`'s former rule 4 removed) |
| Multiple entry/exit criteria — only one needed | `SentryScenarios.Sentry__Given_TwoEntryCriteria__Then_EitherAloneSatisfiesEntry` (#87): a PlanItem with two independent, single-OnPart entry criteria fires on satisfying only the second; the exit-criteria analog is untested — no sample declares a PlanItem with two `<exitCriterion>` elements | Pinned (entry criteria, #87) / KnownGap (exit criteria — no scenario yet, no work item filed) |

## §8.6 Behavior property rules

| Spec rule | Scenario(s) | Status |
|---|---|---|
| 8.6.1 Table 8.12 autoComplete=TRUE | `LifecycleScenarios.TaskLifecycle__…CompleteCompletesCase` (case completes when last child terminal) | Pinned |
| 8.6.1 Table 8.12 autoComplete=FALSE — manual-completion OR-branch (D4) | `KnownGapScenarios.StageCompletion__…ManualCompletionBecomesAvailable` | Pinned (#68 — `StageBehavior.HandleChildTransitioned`'s `UserCompletable` flag-raise condition now gates on `!PlanItemDefinition.AutoComplete` and requires only required children terminal, matching `ManualCompletionCriteriaSatisfied`'s autoComplete=FALSE arm fixed by !26/#19; the observable flag now flips even while a non-required child stays Active) |
| 8.6.2 ManualActivationRule TRUE → Enabled (incl. Table 5.51 default) | `LifecycleScenarios.TaskLifecycle__…NoManualActivationRule…`, `…DisableAndReenable…`, `StageLifecycle__…` | Pinned |
| 8.6.2 ManualActivationRule FALSE → Active | `LifecycleScenarios.TaskLifecycle__…ManualActivationRuleFalse…` | Pinned |
| 8.6.3 RequiredRule evaluated on create; gates parent completion | evaluation-on-create asserted in `KnownGapScenarios.StageCompletion`'s precondition (`Required=true`, now an ordinary green `[Fact]`) and by the existing behavior unit suites; the FULL required-blocks-completion conformance matrix (every combination of required/non-required, autoComplete TRUE/FALSE, and child-state permutations) is not exhaustively scenario-ized beyond the one D4 branch `StageCompletion` pins | Pinned (the one scenario-ized branch, #68) — KnownGap:#19 (remaining matrix combinations; evaluation-on-create covered by unit suites) |
| 8.6.4 RepetitionRule — first evaluation discarded (D7 half) | `SentryScenarios.…RearmsAcrossDistinctSourceInstances` pins `Repeated=false` after first occurrence | Pinned (observable half) |
| 8.6.4 repetition on entry-criterion-with-OnPart satisfaction (detection) | `SentryScenarios.…RearmsAcrossDistinctSourceInstances` (`Repeated=true` on second occurrence) | Pinned |
| 8.6.4 repetition instance creation by owning Stage (Figure 8.6) | `KnownGapScenarios.StageBookkeeping__…StageSpawnsRepetitionInstance` | Pinned (Bug #62, PR !26 `86d7b50`) |
| 8.6.4 repeat-on-complete/terminate (no entry criteria) | `KnownGapScenarios.TaskRepetition__…` | Pinned (#19/D7, PR !26 `d991861`) |
| 8.6.5 ApplicabilityRule filters plannable items | pre-existing `PlanningTableGrainTests` (TRUE/FALSE/nested tables) | Pinned (covered by existing suite; not re-scenario-ized) |

## §8.7 Planning

| Spec rule | Scenario(s) | Status |
|---|---|---|
| Planned PlanItems instantiate when Stage becomes Active | `InstantiationScenarios.Instantiation__Given_MultiplePlanItems__…`; `LifecycleScenarios.StageLifecycle__…` (nested, manual start) | Pinned |
| DiscretionaryItems NOT auto-instantiated (5.4.9.2) | `InstantiationScenarios.Instantiation__Given_PlanningTableDiscretionaryItem__…` | Pinned |
| Nested-stage definitions (5.4.8 declaration inside `<stage>`) | `KnownGapScenarios.NestedDeclaration__…` | Pinned (#65) |
| Run-time planning operation (select discretionary item into the plan) | no public "plan this item into the case" surface exists (`PlanningTableGrain.GetPlannableItems` is query-only) | NotApplicable (no planning-apply surface; M2/M3 roadmap) |
| Table 8.13 planning-allowed states | depends on the planning-apply surface above | NotApplicable (same) |

## §8.3 Case file (Tables 8.1, 8.2) — operations as sentry drivers

| Spec row | Scenario(s) | Status |
|---|---|---|
| create (Ø → Available) drives create-keyed OnParts | pre-existing `CaseFileItemSentryIntegrationTests` flagship; asserted as non-trigger for update/replace/addChild-keyed OnParts throughout this suite | Pinned |
| update drives update-keyed OnParts (and only those) | `SentryScenarios` (AND-join, IfPart scenarios); `CaseFileScenarios.CaseFile__Given_ReplaceKeyedOnPart__…` (update must NOT fire replace) | Pinned |
| replace (distinct standardEvent) | `CaseFileScenarios.CaseFile__Given_ReplaceKeyedOnPart__…` | Pinned |
| addChild | `CaseFileScenarios.CaseFile__Given_AddChildKeyedOnPart__…` | Pinned |
| removeChild / addReference / removeReference | same grain surface and publish path as addChild (`CaseFileItemGrain`); addChild pins the pattern; per-operation scenarios deferred as low-marginal | Pinned (pattern) — remaining ops covered by `CaseFileItemGrainTests` unit/integration suite |
| delete (Available → Discarded, terminal) | `CaseFileScenarios.CaseFile__Given_DeletedItem__…` | Pinned |
| CaseFileItem containment hierarchy semantics (5.3.2 children/sourceRef/targetRefs) | structural-only: `CaseFileItemGrain` has no parent/child propagation; `CmmnCapabilityLint` rule 5 flags it at import | NotApplicable (lint-guarded capability gap; #16 follow-on) |

## Import / lint / deploy pipeline (the suite's own plumbing)

Every scenario transitively pins: `CmmnXmlSerializer.Import` on all 26 sample files,
`CmmnCapabilityLint` clean-pass gating (`DeployAndCreate` throws on `HasUnsupported`),
`ToDeployableCase`, and `Define`/`Create`/`Trigger` deployment. The importer/lint's own
behavior matrix is pinned by the ADO #20 suites (`CmmnXmlSerializerTests`,
`CmmnCapabilityLintTests`, `CmmnImportDeployIntegrationTests`).
