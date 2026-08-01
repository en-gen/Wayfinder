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

**#183 (entry/exit criterion event-type journaling) fix (2026-07-27).**
`StageBehavior`/`TaskBehavior.HandleSentrySatisfied` raised `Host.RaiseEvent(new
EntryCriterionSatisfied {...})` UNCONDITIONALLY, before branching on whether the satisfied
criterion was actually an `EntryCriterion` or an `ExitCriterion`; `PlanItemStore.Apply
(EntryCriterionSatisfied)` then applied it to `EntryCriterionStore` regardless of source —
corrupting the entry-criterion projection AND the persisted journal for any Task/Stage whose exit
criterion fired, including PlanItems with no entry criterion declared at all. `ExitCriterionSatisfied`
was also raised correctly afterward, which is why the functional behavior looked fine; only the
projection and journal were wrong. Fixed by moving the `EntryCriterionSatisfied` raise inside the
`criterion is EntryCriterion` branch in both behaviors. `CasePlanModelBehavior` shares
`StageBehavior.HandleSentrySatisfied` verbatim (no override) and is covered by the same fix.
`MilestoneBehavior`'s own unconditional raise was already correct (it only ever resolves against
`EntryCriteria`, never `ExitCriteria`); `EventListenerBehavior.HandleSentrySatisfied` is a no-op.
Graduated the original one-off reproduction (`Plan/Sentry/Repro/Issue183_*`, which broke this
suite's own convention that every spec-relevant scenario is `.cmmn`-driven and lives in
`Conformance/*Scenarios.cs`) into
`SentryScenarios.Sentry__Given_TaskExitCriterion__Then_EntryCriterionStoreAndJournalStayClean`,
reusing the existing `Sentry_ExitCriterionTask.cmmn` sample — no new sample needed. Added a
positive-path companion, `Sentry_EntryCriterionTask.cmmn` +
`SentryScenarios.Sentry__Given_TaskEntryCriterion__Then_CaseFileEventSatisfiesEntryAndJournalsEntryCriterionSatisfied`,
proving a GENUINE entry-criterion satisfaction still raises and journals `EntryCriterionSatisfied`
correctly (the fix must not over-correct into silently dropping a legitimate event). Also extended
`Sentry__Given_CasePlanModelExitCriterion__Then_CaseFileEventTerminatesCase` with the same
projection/journal assertions at the CasePlanModel root, since `CaseStore.EntryCriterionStore` had
the identical exposure whenever a Case terminated via its own exit criterion — previously
unproven. Added a unit-level regression guard (`Times.Never` on `RaiseEvent<EntryCriterionSatisfied>`)
to both `StageBehaviorTests_HandleSentrySatisfied`'s and
`TaskBehaviorTests_HandleSentrySatisfied`'s existing `…When_ExitCriterion__Then_RaiseEventAndExit`
tests — confirmed by temporarily reverting the fix that this assertion fails against the buggy
code, so it is a genuine regression guard, not a tautology. No consumer of the projection
(`PlanItemStore`, `CaseStore`, `SnapshotMapper`) depended on the spurious event. The project is
pre-1.0 (`CHANGELOG.md` is entirely `[Unreleased]`, no version tags) so no journal-migration
concern applies to any already-persisted case; the fix is forward-only (existing-journal cases
which already recorded the spurious event will still replay corrupted — a known, accepted
limitation, not addressed here).

**#180 fix + follow-up (branch `fix/180-empty-stage-completion`, 2026-07-27).**
`StageBehavior.HandleChildTransitioned` evaluated Table 8.12 completion criteria exclusively in
reaction to a child's own terminal transition, so a Stage with zero `PlanItems` (which produces
zero child transitions) never triggered that evaluation and an `autoComplete=TRUE` empty Stage —
which satisfies "no Active children, all required children terminal" VACUOUSLY over the empty set
— sat Active forever. Fixed by extracting the `autoComplete=TRUE` predicate-and-act out of
`HandleChildTransitioned` into `StageBehavior.TryAutoComplete` and calling it once more, from
`HandleEnterActiveFromStart`, gated on `PlanItemDefinition.AutoComplete &&
!PlanItemDefinition.PlanItems.Any()` — the empty-`PlanItems` gate means the new call is
unreachable for any Stage that has a `PlanItem` at all (including a repeating one, whose
repetition-0 instance is created by the same fan-out this call follows), so it cannot interact
with the repetition/spawn race; the `AutoComplete` gate preserves `autoComplete=FALSE`'s
requirement for explicit completion for an empty Stage. Graduated from a standalone repro test
into three real `.cmmn`-driven scenarios in `LifecycleScenarios.cs` (an empty `<stage>` element
round-trips through `CmmnXmlSerializer`/`CmmnCapabilityLint`/`ToDeployableCase` like any other
sample, so there was no reason to keep it outside this suite): the original vacuous-completion
case, the `autoComplete=FALSE` negative direction (must NOT auto-complete), and an
all-discretionary-children Stage (zero fixed `PlanItems`, a non-null `PlanningTable`) confirming
Table 8.12's `autoComplete=TRUE` column carries no "no DiscretionaryItems pending" term — see
`StageCompletion__Given_AutoCompleteStageWithOnlyDiscretionaryItems__…` and the fix's own remarks
in `StageBehavior.HandleEnterActiveFromStart` for why that conjunct is deliberately absent from the
new gate. (Noted, not fixed here: an empty `autoComplete=TRUE` Stage that ALSO carries a
`RepetitionRule` plus an explicit auto-activation rule would now complete-and-respawn in a loop,
bounded only by the #67 repetition ceiling — a pre-existing hazard class this fix does not
introduce, since pre-fix the Stage was simply wedged instead.)

**#178 fix + #198 discovery (branch `fix/178-zombie-repetition`, 2026-07-30).** #178
(`StageBehavior.HandleChildRepeated` never consulted `Host.State.PlanItemState` before spawning a
repetition child) let a Terminated/Completed/Closed/Suspended/Failed container still spawn one.
Fixed by branching on the container's own state before ever reaching `CreateChild`. While
resolving a suite conflict this fix's own verification surfaced -
`KnownGapScenarios.StageBookkeeping__Given_MilestoneRepetitionDetected__Then_StageSpawnsRepetitionInstance`
started failing once the fix landed - a dedicated investigation found the CasePlanModel's
completion in that scenario was CORRECT: it reached Completed more than two seconds and several
message hops before the scenario's own out-of-band second-instance probe (`BookkeepingProbeB2`)
was even defined, `SourcePlanItem` declares no `repetitionRule` at all
(`Sentry_RearmRepetition.cmmn:29`, so a second instance of it was never modelable in the first
place), and that probe bypasses `StageBehavior.CreateChild` entirely (`GetGrain` + `Define()`
directly), so it never appeared in `StageStore.Children` and could not have blocked completion
even in principle. On `develop` this scenario passed **only by materializing Table 8.9's own
`<impossible>` cell** - a Completed Stage receiving a live-spawned child - and that green status
was the bug, not evidence of conformance. Rewritten to assert the opposite (poll for the
CasePlanModel's completion FIRST, so the assertion cannot race; then drive the late redelivery and
poll `Repeated == true`; then assert NO second instance spawns), retitled to name what it now
actually tests, and graduated out of `KnownGapScenarios` into
`LifecycleScenarios.StageCompletion__Given_LateRepetitionRequestArrivesAfterAutoComplete__Then_RefusesTheSpawn`
(Table 8.9's refusal case, not Bug #62's original spawn-bookkeeping case) - reusing
`Sentry_RearmRepetition.cmmn` as-is, no new sample needed.
    That same investigation, run at scale, found a SECOND, previously-invisible failure:
`KnownGapScenarios.TaskRepetition__Given_RepetitionRuleAndNoEntryCriteria__Then_CompletionSpawnsNewInstance`
failed ~25% of runs on that branch. This was a real, pre-existing engine defect, filed as
[#198](https://github.com/en-gen/Wayfinder/issues/198): `SourceTask`'s completion both (1) raises
`Repeated` and publishes `PlanItemRepetitionCriteriaMetEvent` (`BaseBehavior.
TryRepeatOnCompleteOrTerminate`) and (2) triggers the owning CasePlanModel's Table 8.12 completion
check (`StageBehavior.HandleChildTransitioned`) - on two separate, unordered streams. On
`develop` the completion check ran first roughly 40% of the time, silently completing the
CasePlanModel over what a moment later becomes an Active child - the identical `<impossible>` cell
above, just reached via a different scenario, and never caught because that test's own assertion
only ever checked `instances == 2`, never the container's state. The #178 fix converted that
SILENT violation into a VISIBLE ~25% flake here (it then correctly refused the late spawn once the
completion race was lost). Per the quarantine protocol (ADO #21), this scenario was quarantined at
that point (`[Fact(Skip = "#198 ...")]`) - NOT fixed and NOT weakened, because #198 belonged at the
Table 8.12 completion-evaluation seam, a different piece of work than #178. See
`docs/03-cmmn-execution-semantics.md`'s §8 implementation note and its new Conformance-status row
for #198.
    Running the full suite repeatedly to confirm this was the only intermittent scenario surfaced
the SAME #198 shape - a single no-entry-criteria repeating child under a Stage/CasePlanModel whose
`AutoComplete` defaults to false, no `PlanningTable` - in two more PRE-EXISTING tests outside the
Conformance suite: `Plan/CasePlanModel/RepetitionOnCompletionIntegrationTests.
TaskComplete__Given_NoEntryCriteriaRepeatableTask__Then_FirstEvalDiscardedAndRepetitionSpawnedOnComplete`
and `Plan/CasePlanModel/RepetitionRedeliveryIntegrationTests.
HandleChildRepeated__Given_SameRepetitionCriteriaMetEventDeliveredTwice__Then_ExactlyOneChildIsCreated`
(the latter failed in its own arrange phase, before the #161 redelivery-guard logic it actually
exists to test ever runs). Both were outside this file's own scope (not `.cmmn`-driven Conformance
scenarios), but the SAME root cause and the SAME decision applied: quarantined at that point with a
`[Fact(Skip = "#198 ...")]` citing this same issue - not fixed, not weakened, yet.
`RepetitionGuardFootgunIntegrationTests`
was checked and confirmed SAFE - it deliberately keeps a permanently-Enabled sentinel sibling
specifically so Table 8.12 Branch 1 can never be satisfied while its repetition ceiling test
cascades, which is exactly the kind of guard that routes around this race; every entry-criterion-
driven repetition test (`SentryScenarios`, `SentryRepetitionResetIntegrationTests`,
`RepetitionAfterTerminationIntegrationTests`) uses a different code path
(`HandleSentrySatisfied`, not `TryRepeatOnCompleteOrTerminate`) and is unaffected.

**#198 fix (branch `fix/198-live-repetition-predicate`).** Fixed at the Table 8.12
completion-evaluation seam, where the two prior paragraphs said it belonged — but NOT by ordering
the two streams. `StageBehavior` now holds a Stage's completion while any child is (a) terminal,
(b) `Repeated` on its own live snapshot, and (c) not yet recorded on this container as spawned
(#161's `ChildRepeated`) or definitively refused (the new `RepetitionRequestSettled`). The blocking
signal is the child's own durable state, fetched by direct grain call, so it cannot be lost or
reordered; the clearing signal is produced by the container itself, locally, so it cannot race the
blocking one. The load-bearing ordering fact — that a repeating child is never observable as
terminal before it has confirmed `Repeated`, because `TryRepeatOnCompleteOrTerminate` runs inside
the non-reentrant transition turn — was measured before the fix was written (10 runs of the #198
shape, 42 parent-side reads, zero counterexamples) and is now pinned by
`RepetitionCompletionRaceIntegrationTests.RepeatingChild__…Then_NeverTerminalWithoutRepeated`. The
sibling-driven completion path (the container's check driven by an unrelated child's transition,
which no stream-correlation fix can reach) is pinned by that file's second scenario; the per-branch
behaviour of the gate itself is pinned deterministically at the unit layer
(`StageBehaviorTests_RepetitionCompletionGate.cs`). All three quarantined #198 scenarios
(`KnownGapScenarios.TaskRepetition__…`, `RepetitionOnCompletionIntegrationTests.TaskComplete__…`,
`RepetitionRedeliveryIntegrationTests.HandleChildRepeated__…`) were un-quarantined after 10/10
consecutive green full-suite runs.

**#198 follow-up: the release path (same branch).** Holding a completion is only half a fix —
something has to re-open the decision once the request resolves, and `HandleChildTransitioned`
cannot, because resolving a request produces no child transition Table 8.12 reacts to (a successor
spawned with `ManualActivationRule` TRUE lands `Enabled` and never moves again). Measured on an
`autoComplete=TRUE` container with a non-required repeating child finishing before the last
required child: the gate-only commit left the container stuck `Active` intermittently - this
session's own local measurement put it at 8 stalls in 22 runs (~36%), but an independent
re-measurement on the same commit disagreed, seeing it in only 1 of 22 runs (~4.5%) and 13/13 green
under an equivalent mutation. Treat both as evidence the stall is real and intermittent, not as a
precise rate - it varied substantially between environments and runs. The regression itself is
confirmed real regardless of the exact rate: it produced an actual observed failure at `5c9b007`.
Routing every resolution back through the single Table 8.12 evaluation
(`StageBehavior.TryCompleteStageAfterRepetitionResolved`) closed it. Exercised end-to-end by
`RepetitionCompletionRaceIntegrationTests.AutoCompleteContainer__…Then_TheContainerStillCompletes`
- a useful scenario, but under mutation (both F1 re-evaluation calls reverted) it passed 12/12 in
isolation and again in a full 192-test suite run, so it has effectively no detection power. The
deterministic guard is at the unit layer:
`StageBehaviorTests.Resume__Given_CompletionHeldForABufferedRepetition__Then_DrainReleasesItAndTheStageCompletes`
was the only failure across the entire unit project under that same mutation.
Two adjacent wedge classes are pinned alongside it: a ceiling-refused container still completes
after `Reactivate` (`Reactivate__Given_ARepetitionWasCeilingRefused__…`), and a request stranded
behind a mid-drain ceiling breach is settled rather than left holding completion on a container
nothing will ever re-drain (`Resume__Given_TwoBufferedRepetitions__…`). The Warning that makes a
held completion diagnosable from logs alone is asserted through the real DI-resolved logger in
`RepetitionCompletionRaceIntegrationTests.ManualComplete__…Then_RefusedNamingTheChildAndLoggedAtWarning`
(#194's `FakeLoggerProvider`) and at the unit layer in
`HandleChildTransitioned__Given_CompletionHeldForARepetition__…`.

**Current state (branch `fix/198-live-repetition-predicate`).** 31 sample `.cmmn` files
under `Conformance/Samples/`; 41 scenarios (`CaseFileScenarios` 3, `InstantiationScenarios` 2,
`KnownGapScenarios` 9, `LifecycleScenarios` 16, `SentryScenarios` 11) — **41 executed green, 0
quarantined, 0 failing**. `KnownGapScenarios.cs` keeps its name and the quarantine machinery
described in the honesty rule above; that file's own header remarks still narrate the ORIGINAL
failure investigations (including FINDING-1) for historical context, and the scenario inside it
that #198 had re-quarantined (`TaskRepetition__…`, above) is executing again. This COVERAGE.md
file is the current-status source of truth; where its per-row Status column disagrees with prose
elsewhere, the Status column wins.
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
| Table 8.6 terminate — via CasePlanModel's own exit criteria (8.4.1) | `SentryScenarios.Sentry__Given_CasePlanModelExitCriterion__…CaseFileEventTerminatesCase` (asserts the Case's own terminate, the Table 8.9 cascade to a child Task, AND — #183 — that `CaseStore.EntryCriterionStore`/journal stay clean of a spurious `EntryCriterionSatisfied`, since `CasePlanModelBehavior` shares `StageBehavior.HandleSentrySatisfied` verbatim) | Pinned (#66, #183) |
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
| Table 8.8 fault (Active → Failed; MUST NOT propagate) | `LifecycleScenarios.TaskLifecycle__…FaultReactivateTerminateWalkTable88` (incl. parent-never-Failed assert — NOT parent-stays-Active, which Table 8.12 does not guarantee for this sample's lone-child, autoComplete=false shape; see #221) | Pinned |
| Table 8.8 re-activated (Failed → Active) | `LifecycleScenarios.TaskLifecycle__…FaultReactivateTerminateWalkTable88` | Pinned |
| Table 8.8 complete (Active → Completed) — Task | `LifecycleScenarios.TaskLifecycle__…NoManualActivationRule…` | Pinned |
| Table 8.8 complete — RepetitionRule re-evaluation for no-entry-criteria items | `KnownGapScenarios.TaskRepetition__…CompletionSpawnsNewInstance` — un-quarantined by the #198 fix and green; the owning CasePlanModel can no longer complete in the gap between the child's `Repeated` determination and the delivery of its repetition request (`StageBehavior.RepetitionRequestsAwaitingResolution`) | Pinned |
| Table 8.8 terminate (Active → Terminated, Case worker) | `LifecycleScenarios.TaskLifecycle__…FaultReactivateTerminateWalkTable88` | Pinned |
| Table 8.8 exit — Task (exit criterion while Active) | `SentryScenarios.Sentry__Given_TaskExitCriterion__…CaseFileEventTerminatesActiveTask` (functional); `…Then_EntryCriterionStoreAndJournalStayClean` (#183 — projection/journal fidelity: no spurious `EntryCriterionSatisfied`) | Pinned |
| Table 8.8 exit — Stage (exit criterion while Active; D6 fix) | `SentryScenarios.Sentry__Given_StageExitCriterion__…` | Pinned |
| Table 8.9 exit/terminate propagation to children | `KnownGapScenarios.StageExit__…ExitCascadesTerminationToTask` | Pinned (#63) |
| Table 8.9 fault rows (children keep state on parent fault) | fault non-propagation pinned at the Case level (`TaskLifecycle__…FaultReactivate…`'s parent-never-Failed assert); per-child state matrix not separately scenario-ized | Pinned (non-propagation observable) |
| Table 8.9 complete rows (`<impossible>` cells) — no non-terminal child SURVIVES a completing parent — Stage AND Task columns only; Milestone/EventListener have their own column and legitimately REMAIN Available/Suspended under a completed parent (Table 8.7's completed-Stage description names only "Stage or Task instances"), so those two behaviors are deliberately untouched | `StageCompletionCascadeIntegrationTests.StageCompletionCascade__…StageAndTaskChildrenAreCascadedToTerminatedAndNeverActivate` (a non-required Stage child AND a non-required Task child both left Available/Enabled when their parent auto-completes are cascaded to Terminated via `exit`, so neither can re-activate — the Stage child, additionally, never spawns its own required child) / `…ChildrenAreLeftAlone` (Disabled/Failed children are left untouched, per this table's own rows); model-driven, not a `.cmmn` conformance scenario | Pinned (#179 — `StageBehavior` AND `TaskBehavior.HandleParentTransitioned` both gained a `Complete` case that cascades Exit to any non-terminal child, reusing the existing Exit/Terminate downward-cascade mechanism; `ConfigureForStageOrTask`'s shared state machine and `IsTerminal()` port the same gate verbatim to Task) |
| Table 8.9 complete rows (`<impossible>` cells) — no new child is ADMITTED into a completed parent | `LifecycleScenarios.StageCompletion__Given_LateRepetitionRequestArrivesAfterAutoComplete__Then_RefusesTheSpawn` — a repetition request arriving after its owning CasePlanModel has already, legitimately, completed must be refused, not spawned into the Completed container | Pinned (#178) |

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
| Entry criteria ready while Available | every entry-criterion scenario (milestone/Task wait in Available until satisfied); `SentryScenarios.Sentry__Given_TaskEntryCriterion__…` (#183 companion) additionally asserts the Task's `EntryCriterionSatisfied` is correctly raised AND journaled | Pinned |
| Exit criteria ready while Active (Task and Stage) | `SentryScenarios.Sentry__Given_TaskExitCriterion__…` / `…StageExitCriterion__…` | Pinned |
| Sentry satisfaction raises the event matching the criterion's actual type (Entry vs. Exit), not raised unconditionally | `SentryScenarios.Sentry__Given_TaskExitCriterion__Then_EntryCriterionStoreAndJournalStayClean` (exit side stays clean of a spurious `EntryCriterionSatisfied`, live projection AND persisted journal) / `…Sentry__Given_TaskEntryCriterion__…` (entry side still fires correctly) / `…Sentry__Given_CasePlanModelExitCriterion__…` (same fidelity at the CasePlanModel root) | Pinned (#183) |
| Per-OnPart re-arm across distinct source occurrences (Figure 8.5 B/B′; D5 fix) | `SentryScenarios.Sentry__Given_RepeatableMilestone__…RearmsAcrossDistinctSourceInstances` | Pinned |
| PlanItemOnPart via `sourceRef`/`exitCriterionRef` (Table 5.30 exit mode) | `SentryScenarios.Sentry__Given_PlanItemOnPartWithExitCriterionRef__…` | Pinned (Bug #82 — D10 fix: `PlanItemStateMachine`'s parameterized Exit trigger threads the firing `ExitCriterion`'s own id through `BaseBehavior.HandleTransitioned` into `PlanItemTransitionedEvent.ExitCriterionRef`; `CmmnCapabilityLint`'s former rule 4 removed) |
| Multiple entry/exit criteria — only one needed | `SentryScenarios.Sentry__Given_TwoEntryCriteria__Then_EitherAloneSatisfiesEntry` (#87): a PlanItem with two independent, single-OnPart entry criteria fires on satisfying only the second; the exit-criteria analog is untested — no sample declares a PlanItem with two `<exitCriterion>` elements | Pinned (entry criteria, #87) / KnownGap (exit criteria — no scenario yet, no work item filed) |

## §8.6 Behavior property rules

| Spec rule | Scenario(s) | Status |
|---|---|---|
| 8.6.1 Table 8.12 autoComplete=TRUE | `LifecycleScenarios.TaskLifecycle__…CompleteCompletesCase` (case completes when last child terminal) | Pinned |
| 8.6.1 Table 8.12 autoComplete=TRUE — vacuous satisfaction with zero children (#180) | `LifecycleScenarios.StageCompletion__Given_AutoCompleteStageWithZeroPlanItems__Then_CompletesVacuously` | Pinned (#180 — `StageBehavior.HandleEnterActiveFromStart` now evaluates the shared `TryAutoComplete` predicate once, inline, after its own (here empty) child fan-out, instead of only reactively from `HandleChildTransitioned`) |
| 8.6.1 Table 8.12 autoComplete=FALSE — zero children requires explicit completion (#180 follow-up) | `LifecycleScenarios.StageCompletion__Given_NotAutoCompleteStageWithZeroPlanItems__Then_DoesNotAutoCompleteButManualSucceeds` | Pinned (#180 — the fix's new eager check is gated on `AutoComplete`, so this shape is untouched) |
| 8.6.1 Table 8.12 autoComplete=TRUE — no DiscretionaryItems conjunct, all-discretionary Stage (#180 follow-up) | `LifecycleScenarios.StageCompletion__Given_AutoCompleteStageWithOnlyDiscretionaryItems__Then_CompletesVacuously` | Pinned (#180 — a deliberate reading: the autoComplete=TRUE column has no such term, unlike the autoComplete=FALSE column's Branch 1) |
| 8.6.1 Table 8.12 autoComplete=FALSE — manual-completion OR-branch (D4) | `KnownGapScenarios.StageCompletion__…ManualCompletionBecomesAvailable` | Pinned (#68 — `StageBehavior.HandleChildTransitioned`'s `UserCompletable` flag-raise condition now gates on `!PlanItemDefinition.AutoComplete` and requires only required children terminal, matching `ManualCompletionCriteriaSatisfied`'s autoComplete=FALSE arm fixed by !26/#19; the observable flag now flips even while a non-required child stays Active) |
| 8.6.2 ManualActivationRule TRUE → Enabled (incl. Table 5.51 default) | `LifecycleScenarios.TaskLifecycle__…NoManualActivationRule…`, `…DisableAndReenable…`, `StageLifecycle__…` | Pinned |
| 8.6.2 ManualActivationRule FALSE → Active | `LifecycleScenarios.TaskLifecycle__…ManualActivationRuleFalse…` | Pinned |
| 8.6.3 RequiredRule evaluated on create; gates parent completion | evaluation-on-create asserted in `KnownGapScenarios.StageCompletion`'s precondition (`Required=true`, now an ordinary green `[Fact]`) and by the existing behavior unit suites; the FULL required-blocks-completion conformance matrix (every combination of required/non-required, autoComplete TRUE/FALSE, and child-state permutations) is not exhaustively scenario-ized beyond the one D4 branch `StageCompletion` pins | Pinned (the one scenario-ized branch, #68) — KnownGap:#19 (remaining matrix combinations; evaluation-on-create covered by unit suites) |
| 8.6.4 RepetitionRule — first evaluation discarded (D7 half) | `SentryScenarios.…RearmsAcrossDistinctSourceInstances` pins `Repeated=false` after first occurrence | Pinned (observable half) |
| 8.6.4 repetition on entry-criterion-with-OnPart satisfaction (detection) | `SentryScenarios.…RearmsAcrossDistinctSourceInstances` (`Repeated=true` on second occurrence) | Pinned |
| 8.6.4 repetition instance creation by owning Stage (Figure 8.6) | mechanism itself unchanged and unit-tested (`StageBehaviorTests_HandleChildRepeated_RepetitionGuard.cs` — Bug #62, PR !26 `86d7b50`, still holds for a genuinely Active container); the `.cmmn` scenario that used to demonstrate a successful spawn head-on, `KnownGapScenarios.StageBookkeeping__…`, was retired (its own completion timing made it prove Table 8.9's refusal instead — see `LifecycleScenarios.StageCompletion__Given_LateRepetitionRequestArrivesAfterAutoComplete__…`, above); conformance-level positive-spawn coverage is restored by `TaskRepetition__…` below, un-quarantined by the #198 fix | Pinned |
| 8.6.4 repeat-on-complete/terminate (no entry criteria) | `KnownGapScenarios.TaskRepetition__…` — green after the #198 fix (10/10 consecutive full-suite runs); see the row above and the #198 entry in this file's prose | Pinned |
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

Every scenario transitively pins: `CmmnXmlSerializer.Import` on all 28 sample files,
`CmmnCapabilityLint` clean-pass gating (`DeployAndCreate` throws on `HasUnsupported`),
`ToDeployableCase`, and `Define`/`Create`/`Trigger` deployment. The importer/lint's own
behavior matrix is pinned by the ADO #20 suites (`CmmnXmlSerializerTests`,
`CmmnCapabilityLintTests`, `CmmnImportDeployIntegrationTests`).
