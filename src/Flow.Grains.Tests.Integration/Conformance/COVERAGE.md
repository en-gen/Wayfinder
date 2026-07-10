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

Suite verified at authoring: **33 scenarios — 23 executed green, 10 quarantined (skipped), 0 failing.**

## Engine findings discovered by this suite (details in the #21 report)

| Finding | One-line summary | Work item |
|---|---|---|
| FINDING-1 | Parent→child lifecycle propagation is stream-dead: children subscribe on the parent's *instance* id (`BaseBehavior.Activate`), grains publish on their *definition* id (`CmmnElementGrain.PublishEvent`) — every downward cascade of Tables 8.5/8.6/8.9 never delivers | to be filed |
| FINDING-2 | Auto-start Stages (FALSE ManualActivationRule) crash: the queued `Start` trigger runs `StageBehavior.HandleEnterActiveFromStart` on a non-activation thread — `Host.GrainFactory` throws "Activation access violation", no children instantiate | to be filed |
| FINDING-3 | Definitions declared inside a nested `<stage>` are unresolvable at runtime: the definition index keys definition-id paths, runtime scopes are instance-id paths — only casePlanModel-root declarations resolve | to be filed |

## §8.4.1 Case instance lifecycle (Tables 8.5, 8.6)

| Spec row | Scenario(s) | Status |
|---|---|---|
| Table 8.6 create (Ø → Active, skips Available) | `LifecycleScenarios.CaseCreate__…SkippingAvailable`; every `DeployAndCreate` asserts it implicitly | Pinned |
| Table 8.6 suspend (Active → Suspended), case's own transition | `LifecycleScenarios.CaseLifecycle__…SuspendAndReactivateWalkTable86` | Pinned |
| Table 8.6 suspend — downward propagation (Table 8.5 Suspended MUST) | `KnownGapScenarios.CaseSuspend__…SuspensionPropagatesToTask` | KnownGap:FINDING-1 |
| Table 8.6 terminate (Active → Terminated), case's own transition | `LifecycleScenarios.CaseLifecycle__…TerminateAndReactivateWalkTable86` | Pinned |
| Table 8.6 terminate — downward propagation | `KnownGapScenarios.CaseTerminate__…TerminationPropagatesToMilestone` | KnownGap:FINDING-1 |
| Table 8.6 complete (Active → Completed, via Table 8.12) | `LifecycleScenarios.TaskLifecycle__…CompleteCompletesCase` | Pinned |
| Table 8.6 fault (Active → Failed) | `LifecycleScenarios.CaseLifecycle__…FaultReachesFailedAndReactivateRecovers` | Pinned |
| Table 8.6 re-activate from Failed | `LifecycleScenarios.CaseLifecycle__…FaultReachesFailedAndReactivateRecovers` | Pinned |
| Table 8.6 re-activate from Terminated | `LifecycleScenarios.CaseLifecycle__…TerminateAndReactivateWalkTable86` | Pinned |
| Table 8.6 re-activate from Suspended (case's own transition) | `LifecycleScenarios.CaseLifecycle__…SuspendAndReactivateWalkTable86` | Pinned |
| Table 8.6 re-activate from Suspended — release of cascade-suspended children | `KnownGapScenarios.CaseReactivate__…ChildrenReturnToPriorState` | KnownGap:FINDING-1 + #19 (D8) |
| Table 8.6 re-activate from Completed | — engine permits it (state machine); not scenario-pinned: reactivating a *completed* case is planning-driven (8.7) and planning-at-case-level has no runtime surface yet | NotApplicable (no planning surface) |
| Table 8.6 close (→ Closed) from Completed | `LifecycleScenarios.CaseLifecycle__…ClosesAndStaysClosedAgainstReactivation` | Pinned |
| Table 8.6 close from Terminated/Failed/Suspended | engine state machine permits all three (same `Permit(Close)` wiring pinned from Completed); only the Completed route is scenario-driven | Pinned (Completed route; other from-states covered by the same wiring) |
| Table 8.5 Closed is terminal (no re-activate out of Closed) | `LifecycleScenarios.CaseLifecycle__…StaysClosedAgainstReactivation` | Pinned |
| Table 8.5 Closed — case file becomes read-only, no new planning | no lockdown exists (`CasePlanModelBehavior` has no close handling) | KnownGap:#19 (D8) — documented; no scenario (nothing observable to pin until a lockdown surface exists) |

## §8.4.2 Stage and Task lifecycle (Tables 8.7, 8.8, 8.9)

| Spec row | Scenario(s) | Status |
|---|---|---|
| Table 8.8 create (Ø → Available; Repetition/Required rules evaluated) | `InstantiationScenarios.Instantiation__…TableMandatedStates`; `KnownGapScenarios.StageCompletion` precondition pins `Required=true` | Pinned |
| Table 8.8 enable (Available → Enabled, MAR TRUE) | `LifecycleScenarios.TaskLifecycle__…NoManualActivationRule…` (default TRUE per Table 5.51) | Pinned |
| Table 8.8 start (Available → Active, MAR FALSE) — Task | `LifecycleScenarios.TaskLifecycle__…ManualActivationRuleFalse…` | Pinned |
| Table 8.8 start — STAGE (auto-start + 8.7 instantiation) | `KnownGapScenarios.StageAutoStart__…` | KnownGap:FINDING-2 |
| Table 8.8 manual start (Enabled → Active) — Task | `LifecycleScenarios.TaskLifecycle__…NoManualActivationRule…` | Pinned |
| Table 8.8 manual start — STAGE (+ 8.7 nested instantiation) | `LifecycleScenarios.StageLifecycle__…ManualStartInstantiatesChildren…` | Pinned |
| Table 8.8 disabled (Enabled → Disabled) | `LifecycleScenarios.TaskLifecycle__…DisableAndReenableRoundTrips` | Pinned |
| Table 8.8 re-enable (Disabled → Enabled) | `LifecycleScenarios.TaskLifecycle__…DisableAndReenableRoundTrips` | Pinned |
| Table 8.8 suspended (Active → Suspended, direct) | `LifecycleScenarios.StageLifecycle__…SuspendResumeWorks` (Stage); Task variant via parent-cascade only — see parent suspend row | Pinned (Stage direct) |
| Table 8.8 resume (Suspended → Active, direct) | `LifecycleScenarios.StageLifecycle__…SuspendResumeWorks` | Pinned |
| Table 8.8 parent suspend / parent resume (+ Table 8.9 note (2)) | `KnownGapScenarios.StageSuspend__…TaskFollowsByPropagationOnly` | KnownGap:FINDING-1 |
| Table 8.8 fault (Active → Failed; MUST NOT propagate) | `LifecycleScenarios.TaskLifecycle__…FaultReactivateTerminateWalkTable88` (incl. parent-still-Active assert) | Pinned |
| Table 8.8 re-activated (Failed → Active) | `LifecycleScenarios.TaskLifecycle__…FaultReactivateTerminateWalkTable88` | Pinned |
| Table 8.8 complete (Active → Completed) — Task | `LifecycleScenarios.TaskLifecycle__…NoManualActivationRule…` | Pinned |
| Table 8.8 complete — RepetitionRule re-evaluation for no-entry-criteria items | `KnownGapScenarios.TaskRepetition__…CompletionSpawnsNewInstance` | KnownGap:#19 (D7) |
| Table 8.8 terminate (Active → Terminated, Case worker) | `LifecycleScenarios.TaskLifecycle__…FaultReactivateTerminateWalkTable88` | Pinned |
| Table 8.8 exit — Task (exit criterion while Active) | `SentryScenarios.Sentry__Given_TaskExitCriterion__…` | Pinned |
| Table 8.8 exit — Stage (exit criterion while Active; D6 fix) | `SentryScenarios.Sentry__Given_StageExitCriterion__…` | Pinned |
| Table 8.9 exit/terminate propagation to children | `KnownGapScenarios.StageExit__…ExitCascadesTerminationToTask` | KnownGap:FINDING-1 |
| Table 8.9 fault rows (children keep state on parent fault) | fault non-propagation pinned at the Case level (`TaskLifecycle__…FaultReactivate…`'s parent-still-Active assert); per-child state matrix not separately scenario-ized | Pinned (non-propagation observable) |
| Table 8.9 complete rows (`<impossible>` cells) | blocked behind Table 8.12 gaps (D4) — completing a stage with children in the listed states isn't reachable through the public surface today | KnownGap:#19 (D4) — via StageCompletion scenario |

## §8.4.3 EventListener and Milestone lifecycle (Tables 8.10, 8.11)

| Spec row | Scenario(s) | Status |
|---|---|---|
| Table 8.11 create (Ø → Available) | `LifecycleScenarios.MilestoneLifecycle__…WalkTable811`; `InstantiationScenarios` | Pinned |
| Table 8.11 suspend (Available → Suspended) | `LifecycleScenarios.MilestoneLifecycle__…WalkTable811` | Pinned |
| Table 8.11 resume (Suspended → Available) | `LifecycleScenarios.MilestoneLifecycle__…WalkTable811` | Pinned |
| Table 8.11 terminate (Available → Terminated) | `LifecycleScenarios.MilestoneLifecycle__…WalkTable811` | Pinned |
| Table 8.11 occur — Milestone (achieving sentry satisfied) | `SentryScenarios` (every milestone-completing scenario) | Pinned |
| Table 8.11 occur — EventListener (timer/user event) | timer start-trigger runtime is pinned by the pre-existing `CaseFileItemSentryIntegrationTests` timer scenario and `Scheduler` suites; UserEventListener occurrence needs role setup outside this suite's `.cmmn`-driven scope today | NotApplicable (covered elsewhere / role surface out of scope) |
| Table 8.11 parent terminate | `KnownGapScenarios.CaseTerminate__…PropagatesToMilestone` | KnownGap:FINDING-1 |

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
| PlanItemOnPart via `sentryRef`/`exitCriterionRef` (Table 5.30 exit mode) | unreachable: `PlanItemTransitionedEvent.ExitCriterionRef` is never populated (D10 remainder; `CmmnCapabilityLint` rule 4 flags it on import) | KnownGap:#19-adjacent (D10) — lint-guarded, no runnable scenario |
| Multiple entry/exit criteria — only one needed | single-criterion scenarios only; multi-criteria OR is untested pending D4/D10 work | KnownGap:#19 — documented, scenario deferred until the D-item work lands |

## §8.6 Behavior property rules

| Spec rule | Scenario(s) | Status |
|---|---|---|
| 8.6.1 Table 8.12 autoComplete=TRUE | `LifecycleScenarios.TaskLifecycle__…CompleteCompletesCase` (case completes when last child terminal) | Pinned |
| 8.6.1 Table 8.12 autoComplete=FALSE — manual-completion OR-branch (D4) | `KnownGapScenarios.StageCompletion__…ManualCompletionBecomesAvailable` | KnownGap:#19 (D4) |
| 8.6.2 ManualActivationRule TRUE → Enabled (incl. Table 5.51 default) | `LifecycleScenarios.TaskLifecycle__…NoManualActivationRule…`, `…DisableAndReenable…`, `StageLifecycle__…` | Pinned |
| 8.6.2 ManualActivationRule FALSE → Active | `LifecycleScenarios.TaskLifecycle__…ManualActivationRuleFalse…` | Pinned |
| 8.6.3 RequiredRule evaluated on create; gates parent completion | evaluation-on-create asserted in `KnownGapScenarios.StageCompletion`'s precondition (`Required=true`; currently dormant with its skip) and by the existing behavior unit suites; the required-blocks-completion conformance matrix is deferred with D4 | KnownGap:#19 (completion-gating matrix; evaluation covered by unit suites) |
| 8.6.4 RepetitionRule — first evaluation discarded (D7 half) | `SentryScenarios.…RearmsAcrossDistinctSourceInstances` pins `Repeated=false` after first occurrence | Pinned (observable half) |
| 8.6.4 repetition on entry-criterion-with-OnPart satisfaction (detection) | `SentryScenarios.…RearmsAcrossDistinctSourceInstances` (`Repeated=true` on second occurrence) | Pinned |
| 8.6.4 repetition instance creation by owning Stage (Figure 8.6) | `KnownGapScenarios.StageBookkeeping__…StageSpawnsRepetitionInstance` | KnownGap:#62 |
| 8.6.4 repeat-on-complete/terminate (no entry criteria) | `KnownGapScenarios.TaskRepetition__…` | KnownGap:#19 (D7) |
| 8.6.5 ApplicabilityRule filters plannable items | pre-existing `PlanningTableGrainTests` (TRUE/FALSE/nested tables) | Pinned (covered by existing suite; not re-scenario-ized) |

## §8.7 Planning

| Spec rule | Scenario(s) | Status |
|---|---|---|
| Planned PlanItems instantiate when Stage becomes Active | `InstantiationScenarios.Instantiation__Given_MultiplePlanItems__…`; `LifecycleScenarios.StageLifecycle__…` (nested, manual start) | Pinned |
| DiscretionaryItems NOT auto-instantiated (5.4.9.2) | `InstantiationScenarios.Instantiation__Given_PlanningTableDiscretionaryItem__…` | Pinned |
| Nested-stage definitions (5.4.8 declaration inside `<stage>`) | `KnownGapScenarios.NestedDeclaration__…` | KnownGap:FINDING-3 |
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

Every scenario transitively pins: `CmmnXmlSerializer.Import` on 18 sample files,
`CmmnCapabilityLint` clean-pass gating (`DeployAndCreate` throws on `HasUnsupported`),
`ToDeployableCase`, and `Define`/`Create`/`Trigger` deployment. The importer/lint's own
behavior matrix is pinned by the ADO #20 suites (`CmmnXmlSerializerTests`,
`CmmnCapabilityLintTests`, `CmmnImportDeployIntegrationTests`).
