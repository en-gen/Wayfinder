using System;
using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors.Stores;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Flow.Grains.Plan.PlanningTable;
using Flow.Grains.Plan.Sentry;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public class StageBehavior : BaseBehavior<Stage>
    {
        private StageBehaviorStore StageStore => Host.State.BehaviorExtension as StageBehaviorStore ?? throw new InvalidOperationException();

        // ADO #66 - override point for the CasePlanModel/ordinary-Stage-or-Task asymmetry
        // ~~~~~
        // Table 5.31's transition glossary scopes `exit` to "the Stage or Task" only (the
        // casePlanModel is deliberately omitted) and `terminate` to "the casePlanModel, Stage, or
        // Task". HandleSentrySatisfied below is shared verbatim by CasePlanModelBehavior, so this
        // is the one seam that lets the CasePlanModel fire a different trigger for the exact same
        // "my own exit criterion was satisfied" event, without duplicating the rest of the method.
        // Ordinary Stage/Task instances keep the base Exit; CasePlanModelBehavior overrides this to
        // Terminate (see PlanItemStateMachine.ConfigureForCasePlanModel's remarks for why Table 8.6
        // has no `exit` row for the casePlanModel to permit in the first place).
        protected virtual PlanItemTransition ExitCriterionTransition => PlanItemTransition.Exit;

        // ADO #67 - RepetitionGuardOptions.MaxRepetitionsPerPlanItem, threaded through from
        // PlanItemBehaviorConfiguratorService (IOptions<RepetitionGuardOptions>). Defaulted here
        // (rather than required) so every existing direct `new StageBehavior(...)` in the unit
        // test suite keeps compiling against the generous default without modification - only
        // production DI and tests specifically targeting the ceiling need to pass it explicitly.
        // See HandleChildRepeated for enforcement and RepetitionGuardOptions for the full
        // rationale (Case.Flow ENGINE EXTENSION, not CMMN spec surface).
        private readonly int _repetitionCeiling;

        public StageBehavior(
            IBehaviorHost host,
            Stage planItemDefinition,
            IPlanItemStateMachine stateMachine,
            int repetitionCeiling = RepetitionGuardOptions.DefaultMaxRepetitionsPerPlanItem) :
            base(host, planItemDefinition, stateMachine)
        {
            _repetitionCeiling = repetitionCeiling;

            StateMachine.Configure(PlanItemState.Available)
                .OnEntryFromAsync(PlanItemTransition.Create, HandleEnterAvailableFromCreate);

            StateMachine.Configure(PlanItemState.Active)
                .OnEntryFromAsync(PlanItemTransition.Start, HandleEnterActiveFromStart)
                .OnEntryFromAsync(PlanItemTransition.ManualStart, HandleEnterActiveFromStart);
        }

        public override Task Activate() =>
            Task.WhenAll(
                base.Activate(),
                Task.WhenAll(PlanItemDefinition.PlanItems.Select(planItem =>
                    Task.WhenAll(
                        Host.SubscribeTo<PlanItemTransitionedEvent>(
                            planItem.Id,
                            HandleChildTransitioned,
                            StreamFlags.Resume),
                        Host.SubscribeTo<PlanItemRepetitionCriteriaMetEvent>(
                            planItem.Id,
                            HandleChildRepeated,
                            StreamFlags.Resume)))));
        

        protected override Task Define() =>
            Task.WhenAll(
                // based on interpretation of 8.7 - Available, EntryCriterion may occur
                // before parent is Active, and therefor before this is available
                SubscribeToCriteria(x => x.EntryCriteria, StreamFlags.Create | StreamFlags.Resume),
                // define sentries
                Task.WhenAll(PlanItemDefinition.Sentries
                    .Select(async sentry =>
                    {
                        var sentryInstanceId = ShortGuid.NewGuid();

                        var sentryGrain = Host.GrainFactory
                            .GetGrain<ISentryGrain>(Host.CaseInstanceId, $"{Host.Address}.{sentryInstanceId}");

                        if (await sentryGrain.Defined()) return;

                        Host.LogWithContext(logger => logger.LogInformation(
                            "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | creating sentry {SentryId}",
                            Host.Definition.GetType().Name,
                            PlanItemDefinition.GetType().Name,
                            Host.Scope,
                            Host.InstanceId,
                            sentryInstanceId));

                        await sentryGrain.Define(Host.State.CaseDefinitionId, sentry);

                        // TODO: should raise an event to catalog sentry instance?
                    })),
                // define planning table
                DefinePlanningTable());

        private async Task DefinePlanningTable()
        {
            if (PlanItemDefinition.PlanningTable == null) return;

            var planningTableGrain = Host.GrainFactory
                .GetGrain<IPlanningTableGrain>(Host.CaseInstanceId, Host.InstanceId);

            if (await planningTableGrain.Defined()) return;

            await planningTableGrain.Define(Host.State.CaseDefinitionId, PlanItemDefinition.PlanningTable);
        }

        // 8.8 - Stage and Task instance transitions
        // ~~~~~
        // Create: Transition to the initial state (Available) when the Stage or Task
        // instance is created. This happens when the Stage instance
        // containing this Stage or Task instance transitions to Active. The
        // RepetitionRule Boolean expression MUST be evaluated in this
        // transition. The RequiredRule Boolean expression MUST be
        // evaluated in this transition, and its Boolean value SHOULD be
        // maintained for the rest of the life of the Stage or Task instance.
        // ==========
        // D6 - exit-criteria subscription on the create path
        // ~~~~~
        // 8.5: "Exit criterion sentries are considered ready for evaluation while the ... Stage ...
        // is in Active state." A Stage's ExitCriteria are only reachable (StateMachine.CanFire
        // (PlanItemTransition.Exit) from Available/Enabled/Active/etc. - see
        // PlanItemStateMachine.ConfigureForStageOrTask) once this create path has already run, so
        // there is no later "when Active" moment that separately arms the subscription - it MUST
        // happen here, same as EntryCriteria a few lines above and same as
        // TaskBehavior.HandleEnterAvailableFromCreate already does for its own ExitCriteria.
        // Before this fix, only base.Activate()'s Resume-only subscription
        // (BaseBehavior.Activate -> SubscribeToCriteria(x => x.ExitCriteria, StreamFlags.Resume))
        // ever touched this stream; CmmnElementGrain.SubscribeTo's Resume branch is a no-op unless a
        // subscription handle already exists (GetAllSubscriptionHandles().Any()), and nothing ever
        // created one - so a Stage's exit criteria were not merely delayed until a
        // deactivate/reactivate cycle, they were never subscribed at all. Unlike TaskBehavior's
        // ExitCriteria (gated by IsBlocking - a non-blocking Task completes immediately and 5.24
        // forbids it from declaring exitCriteriaRefs at all), Table 5.34 places no equivalent
        // condition on a Stage's ExitCriteria, so this subscription is unconditional.
        // Sequential, not Task.WhenAll (#19, found via the D7 e2e): interleaving
        // EvaluateRequiredRule's internal ConfirmEvents with EnableOrStart's nested
        // FireAsync(Enable) (whose HandleTransitioned raises + confirms again) on the same
        // journaled-grain adaptor intermittently NREs reading TentativeState mid-confirm -
        // observed as a racy "Object reference not set" inside HandleTransitioned's log action
        // during Trigger(Create), and, when the create arrives via a stream-delivery turn
        // (HandleChildRepeated -> CreateChild), swallowed silently by the streaming agent
        // (= Bug #62's missing repetitions). Rule evaluations stay ordered before any
        // transition cascade; subscriptions are armed before EnableOrStart so nothing the
        // cascade produces can be missed.
        private async Task HandleEnterAvailableFromCreate()
        {
            await EvaluateRepetitionRule(discard: true);
            await EvaluateRequiredRule();

            await SubscribeToCriteria(x => x.ExitCriteria, StreamFlags.Create);

            // 8.7 - Stage and Task instances states
            // ~~~~~
            // While available, the Stage or Task instance is waiting for its entry criteria (Sentry) to become TRUE.
            // A missing entry criteria(Sentry) is considered TRUE.
            if (Host.Definition.EntryCriteria.Any())
            {
                await SubscribeToCriteria(x => x.EntryCriteria, StreamFlags.Create);
            }
            else
            {
                await EnableOrStart();
            }
        }

        private async Task EnableOrStart()
        {
            if (await EvaluateManualActivationRule())
            {
                await StateMachine.FireAsync(PlanItemTransition.Enable);
            }
            else
            {
                await StateMachine.FireAsync(PlanItemTransition.Start);
            }
        }

        // Shared with CasePlanModelBehavior: 8.6.1 Stage semantics describe child PlanItem
        // instantiation as happening on entry to Active, regardless of which trigger reaches
        // Active (Start/ManualStart for an ordinary Stage; Create for the CasePlanModel's
        // outermost Stage - see Table 8.6/Table 5.31, and CasePlanModelBehavior's remarks).
        //
        // 5.4.9.2/8.7 - DiscretionaryItem / Planning
        // ~~~~~
        // Only PlanItemDefinition.PlanItems (the plan) are instantiated here.
        // PlanningTable.DiscretionaryItems are NOT instantiated on entry to Active: a
        // DiscretionaryItem instance is planned "to the discretion" of a Case worker, moved
        // into the plan only when a worker selects it from the PlanningTable at run-time
        // (8.7 Planning). Auto-instantiating them here would contradict that definition.
        //
        // ConfirmEvents() here (found necessary while wiring CasePlanModelBehavior, #55): Stateless
        // invokes StateMachine.OnTransitionedAsync's callback (BaseBehavior.HandleTransitioned,
        // which raises Transitioned and confirms it) BEFORE the destination state's
        // OnEntryFromAsync action - i.e. THIS method runs after that confirm has already
        // happened, not before. Each CreateChild call below raises a ChildCreated event on THIS
        // Stage/CasePlanModel instance (not the child - the child confirms its own Defined/
        // Transitioned independently), and nothing later in the current transition confirms
        // them, so without an explicit confirm here they remain queued in TentativeState
        // indefinitely. Confirmed once after all children are created rather than per-child:
        // ConfirmEvents() persists everything queued so far, so one call covers the batch.
        protected async Task HandleEnterActiveFromStart()
        {
            await Task.WhenAll(PlanItemDefinition.PlanItems
                .Select(x => CreateChild(x)));

            await Host.ConfirmEvents();
        }

        protected override async Task HandleSentrySatisfied(SentrySatisfiedEvent @event, StreamSequenceToken token = null)
        {
            // 5.4.5.1 - Criterion, Table 5.25 - Criterion attributes
            // ~~~~~
            // Reference a Sentry that represents the PlanItem’s entry or exit criteria.
            // Criteria of a PlanItem MUST refer to Sentries that are contained by the
            // Stage or PlanFragment that contains that PlanItem.
            if (Host.Scope != @event.SourceScope) return;

            var criterion = Host.Definition
                .EntryCriteria.Union<Criterion>(Host.Definition.ExitCriteria)
                .SingleOrDefault(x => x.SentryRef == @event.SourceDefinitionId);

            // disregard events in our scope, but are not our concern
            if (criterion == null) return;

            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | {CriterionType} {CriterionId} satisfied by sentry {SentryRef}.  OnPart: {OnPartOccurred}",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                criterion.GetType().Name,
                criterion.Id,
                @event.SourceDefinitionId,
                @event.OnPartOccurred));

            Host.RaiseEvent(new EntryCriterionSatisfied
            {
                SourceScope = @event.SourceScope,
                SourceId = @event.SourceDefinitionId,
                OnPartOccurred = @event.OnPartOccurred
            });

            if (criterion is EntryCriterion)
            {
                // disregard entry criteria if already repeated
                //   - just being defensive. this should never happen as entry criteria
                //     subs are removed on repeat
                if (Host.State.Repeated) return;

                if (@event.OnPartOccurred && Host.State.PlanItemState == PlanItemState.Uninitialized)
                {
                    await StateMachine.FireAsync(PlanItemTransition.Create);
                }

                if (Host.State.PlanItemState == PlanItemState.Available)
                {
                    // 8.6.2 ManualActivationRule
                    // ~~~~~
                    // If this rule evaluates to TRUE, the Task or Stage instance transitions from Available
                    // to Enabled (via Enable), otherwise it transitions from Available to Active (via Start).
                    if (await EvaluateManualActivationRule())
                    {
                        await StateMachine.FireAsync(PlanItemTransition.Enable);
                    }
                    else
                    {
                        await StateMachine.FireAsync(PlanItemTransition.Start);
                    }
                }
                // 8.6.4 RepetitionRule
                // ~~~~~
                // (after transitioning from Available), every time an entry criterion with an OnPart is satisfied
                // the RepetitionRule’s condition is re-evaluated and if it evaluates to TRUE, a new instance of the
                // Task, Stage, or Milestone is created and transition to Available
                else if (@event.OnPartOccurred && await EvaluateRepetitionRule())
                {
                    await Task.WhenAll(
                        UnsubscribeFromCriteria(x => x.EntryCriteria),
                        Host.Publish(new PlanItemRepetitionCriteriaMetEvent(
                            Host.Scope,
                            Host.InstanceId,
                            Host.DefinitionId,
                            Host.State.Repetition)));

                    Host.RaiseEvent(new Repeated());
                }
            }
            else if (criterion is ExitCriterion &&
                     StateMachine.CanFire(ExitCriterionTransition))
            {
                Host.RaiseEvent(new ExitCriterionSatisfied
                {
                    SourceScope = @event.SourceScope,
                    SourceId = @event.SourceDefinitionId,
                    OnPartOccurred = @event.OnPartOccurred
                });

                // D10 - only the Exit trigger carries a parameterized ExitCriterionRef (see
                // PlanItemStateMachine.FireAsync(PlanItemTransition, string) / BaseBehavior.
                // HandleTransitioned). ExitCriterionTransition is Terminate for the CasePlanModel
                // (CasePlanModelBehavior's override - Table 8.6 has no `exit` row for it), which
                // has no such registered trigger parameter, so that path keeps firing plain.
                if (ExitCriterionTransition == PlanItemTransition.Exit)
                {
                    await StateMachine.FireAsync(PlanItemTransition.Exit, criterion.Id);
                }
                else
                {
                    await StateMachine.FireAsync(ExitCriterionTransition);
                }
            }
        }

        protected override async Task HandleParentTransitioned(PlanItemTransitionedEvent @event, StreamSequenceToken token = null)
        {
            // ignore if not from direct parent
            if (@event.SourceInstanceId != Host.ParentInstanceId) return;

            PlanItemTransition? transition = null;
            switch (@event.StandardEvent)
            {
                case PlanItemTransition.Suspend:
                case PlanItemTransition.ParentSuspend:
                {
                    Host.RaiseEvent(new ParentSuspended());
                    transition = PlanItemTransition.ParentSuspend;
                    break;
                }
                case PlanItemTransition.Resume:
                case PlanItemTransition.ParentResume:
                // Table 8.6 re-activate + Table 8.9 note (2) (#63 D8 carve-out): the Case leaves
                // Suspended via Reactivate, not Resume (ConfigureForCasePlanModel permits
                // Reactivate, not Resume/ParentResume, from Suspended) - so a cascaded-suspended
                // direct child of the CasePlanModel would otherwise never see a transition its
                // switch recognizes. Folded into the same arm as Resume/ParentResume: the
                // ParentResume state-machine permit is already guarded by
                // ParentSuspendState.HasValue (PlanItemStateMachine.ConfigureForStageOrTask), so
                // StateMachine.CanFire below naturally no-ops for a child that wasn't cascaded
                // into Suspended (e.g. the case reactivating from Completed/Terminated/Failed).
                case PlanItemTransition.Reactivate:
                {
                    Host.RaiseEvent(new ParentResumed());
                    transition = PlanItemTransition.ParentResume;
                    break;
                }
                case PlanItemTransition.Exit:
                case PlanItemTransition.Terminate:
                {
                    Host.RaiseEvent(new ParentTerminated());
                    transition = PlanItemTransition.Exit;
                    break;
                }
            }

            if (transition.HasValue && StateMachine.CanFire(transition.Value))
            {
                await StateMachine.FireAsync(transition.Value);
            }
        }

        private async Task HandleChildTransitioned(PlanItemTransitionedEvent @event, StreamSequenceToken token = null)
        {
            // ignore events that are not direct children
            if (@event.SourceScope != Host.Address) return;

            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | processing instance {ChildElementInstanceId} of child {ChildElementDefinitionId} transition {PreviousState} × {StandardEvent} = {CurrentState}",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                @event.SourceInstanceId,
                @event.SourceDefinitionId,
                @event.Source,
                @event.StandardEvent,
                @event.Destination));

            // 8.7 - Stage and Task instances states
            // ~~~~~
            // The (child) Stage or Task has a Sentry with an OnPart that has a sourceRef outside of the enclosing
            // Stage of that Stage or Task and this Sentry is satisfied. In that case, the Stage instance and
            // recursively all Stage instances up to the enclosing Stage of the Stage or Task in which the
            // Stage or Task resides moves into Active state if not already active.
            //
            // Note: we are interpreting this as overriding any ManualActivationRule
            if (Host.State.PlanItemState < PlanItemState.Active && @event.Destination == PlanItemState.Available)
            {
                Host.RaiseEvent(new ChildTriggeredActivation());
                if (Host.State.PlanItemState == PlanItemState.Uninitialized)
                {
                    await StateMachine.FireAsync(PlanItemTransition.Create);
                }
                if (Host.State.PlanItemState == PlanItemState.Available)
                {
                    await StateMachine.FireAsync(PlanItemTransition.Start);
                }
                if (Host.State.PlanItemState == PlanItemState.Disabled)
                {
                    await StateMachine.FireAsync(PlanItemTransition.Reenable);
                }
                if (Host.State.PlanItemState == PlanItemState.Enabled)
                {
                    await StateMachine.FireAsync(PlanItemTransition.ManualStart);
                }
            }

            var childSnapshots = await GetChildSnapshots();

            // Table 8.12 - UserCompletable (#68, D4 residual): the UI-facing observability twin
            // of ManualCompletionCriteriaSatisfied's autoComplete=FALSE arm below, not of the
            // autoComplete=TRUE column. The two columns are genuinely different completion
            // branches: autoComplete=TRUE has NO Manual Completion branch in Table 8.12 at all -
            // it auto-completes on its own with no human involvement (the AutoComplete branch
            // further down) - while autoComplete=FALSE's Manual Completion OR-branch requires
            // only that REQUIRED children be terminal, carrying no "no Active children" conjunct
            // (a still-Active non-required child must not block it - the original D4 finding).
            // The prior condition here conflated both: it ignored AutoComplete entirely (so an
            // autoComplete=TRUE stage got latched "user-completable" even though a human cannot
            // legitimately manually-complete it in that mode) and required ALL children
            // non-Active - not just required ones - before flipping the flag (so an
            // autoComplete=FALSE stage with a lingering non-required Active child never latched,
            // even after !26/#19 fixed the actual Trigger(Complete) enforcement gate to permit
            // exactly that). Gating on !PlanItemDefinition.AutoComplete and dropping the
            // no-Active-children conjunct keeps this flag true only where the spec's Manual
            // Completion branch actually is.
            if (!PlanItemDefinition.AutoComplete &&
                !Host.State.UserCompletable &&
                StateMachine.CanFire(PlanItemTransition.Complete) &&
                childSnapshots.Where(x => x.Required).All(x => x.PlanItemState.IsTerminal()))
            {
                Host.RaiseEvent(new UserCompletableCriteriaMet());
            }

            // Table 8.12 - Stage instance termination criteria
            // ~~~~~
            // autoComplete = TRUE
            // There are no Active children, AND all required (requiredRule evaluates to TRUE) children are
            // in {Disabled, Completed, Terminated, Failed}.
            //
            // autoComplete = FALSE
            // (There are no Active children AND all children are in {Disabled, Completed, Terminated, Failed}
            // AND there are no DiscretionaryItems) OR (Manual Completion AND all required (requiredRule evaluates
            // to TRUE) children are in { Disabled, Completed, Terminated, Failed}).
            //
            // These are two INDEPENDENT OR-branches, not one combined condition: Branch 1 (below)
            // is the automatic path this method evaluates on every child transition; Branch 2
            // ("Manual Completion") is NOT evaluated here at all - it is an explicitly-invoked
            // action (an external Trigger(Complete) call), gated by Trigger's override below,
            // and deliberately does not require non-required children to also be done.
            if (@event.Destination.IsTerminal())
            {
                if (PlanItemDefinition.AutoComplete)
                {
                    // ...There are no Active children
                    if (childSnapshots.All(x => x.PlanItemState != PlanItemState.Active) &&
                        // ... all required children are in {Disabled, Completed, Terminated, Failed}
                        childSnapshots.Where(x => x.Required).All(x => x.PlanItemState.IsTerminal()) &&
                        StateMachine.CanFire(PlanItemTransition.Complete))
                    {
                        Host.RaiseEvent(new AutoCompleteCriteriaMet());
                        await StateMachine.FireAsync(PlanItemTransition.Complete);
                    }
                }
                // Branch 1: ...There are no Active children AND all children (not just required
                // ones) are in {Disabled, Completed, Terminated, Failed} AND there are no
                // DiscretionaryItems left that a Case worker could still plan. "No
                // DiscretionaryItems" is a standalone structural condition - it must not depend on
                // Host.State.UserCompletable (a latched UI hint related to Branch 2, a different
                // OR-branch entirely); conflating the two here was the original bug: a stage whose
                // PlanningTable's items were all already planned could only complete if the
                // UserCompletable latch happened to be set, and (because .All() on an empty
                // sequence is true) a PlanningTable with zero DiscretionaryItems demanded the same
                // latch for no reason. NOTE the null-check IS still doing double duty as "no
                // DiscretionaryItems": an absent PlanningTable and a present-but-fully-planned one
                // are both correctly "none pending" - .All() covers the empty table too.
                else
                {
                    var noDiscretionaryItemsPending =
                        PlanItemDefinition.PlanningTable == null ||
                        PlanItemDefinition.PlanningTable.DiscretionaryItems
                            .All(di => childSnapshots.Any(snap => snap.Definition.Id == di.Id));

                    if (childSnapshots.All(x => x.PlanItemState.IsTerminal()) &&
                        noDiscretionaryItemsPending &&
                        StateMachine.CanFire(PlanItemTransition.Complete))
                    {
                        Host.RaiseEvent(new FullyCompleteCriteriaMet());
                        await StateMachine.FireAsync(PlanItemTransition.Complete);
                    }
                }
            }
        }

        // Table 8.12 - Stage instance termination criteria, evaluated LIVE at the moment of an
        // externally-invoked (manual) Complete - a Case worker completing a Stage by hand is
        // exactly a Trigger(Complete) call arriving from outside (via IPlanItemGrain.Trigger /
        // ICaseGrain.Trigger), as opposed to Branch 1 above, which HandleChildTransitioned
        // evaluates and fires automatically on its own.
        //
        // autoComplete=FALSE, Branch 2 ("Manual Completion AND all required (requiredRule
        // evaluates to TRUE) children are in {Disabled, Completed, Terminated, Failed}"): read the
        // OR-structure precisely - unlike Branch 1 and unlike the autoComplete=TRUE column, this
        // branch does NOT carry a "there are no Active children" conjunct and does NOT care about
        // pending DiscretionaryItems. A still-Active NON-required child therefore must not block
        // manual completion - which is the D4 headline: previously no manual-completion gate
        // existed at all, so an external Complete fell through to the bare Stateless permit with
        // no Table 8.12 check whatsoever, while the only criteria evaluation that DID exist (the
        // automatic one above) required ALL children terminal.
        //
        // autoComplete=TRUE has no manual branch in Table 8.12 - its single column IS the
        // completion criteria for any complete transition (8.8/Table 8.8: "For a Stage instance,
        // the termination criteria described in Table 8.12 ... must be satisfied"), so a manual
        // Complete on an autoComplete stage is held to that same column: no Active children AND
        // required children terminal.
        //
        // Evaluated live from child snapshots rather than from the latched
        // Host.State.UserCompletable flag: the latch is monotonic (never reset), so it both
        // understates (a stage whose only blocker was a non-required Active child never latched
        // under the old combined condition) and can overstate (a repetition spawning a NEW
        // required child after the latch would leave it stale-true). The latched event remains as
        // a UI hint; enforcement reads current state.
        protected virtual async Task<bool> ManualCompletionCriteriaSatisfied()
        {
            var childSnapshots = await GetChildSnapshots();

            var requiredChildrenTerminal = childSnapshots
                .Where(x => x.Required)
                .All(x => x.PlanItemState.IsTerminal());

            return PlanItemDefinition.AutoComplete
                ? childSnapshots.All(x => x.PlanItemState != PlanItemState.Active) && requiredChildrenTerminal
                : requiredChildrenTerminal;
        }

        // Gate ONLY an externally-invoked Complete, and only when the state machine could
        // otherwise fire it - a Complete attempted from a non-Active state still falls through to
        // the existing silent unhandled-trigger no-op (BaseBehavior.HandleUnhandledTrigger), same
        // as every other invalid transition. When the machine is willing but Table 8.12 is not,
        // fail loudly: this is a rule violation at the public surface, not an idempotent replay.
        public override async Task Trigger(PlanItemTransition transition)
        {
            if (transition == PlanItemTransition.Complete &&
                StateMachine.CanFire(PlanItemTransition.Complete) &&
                !await ManualCompletionCriteriaSatisfied())
            {
                throw new InvalidOperationException(
                    $"stage {Host.Scope}.{Host.InstanceId} does not satisfy Table 8.12's completion criteria: " +
                    (PlanItemDefinition.AutoComplete
                        ? "autoComplete=true requires no Active children and all required children terminal"
                        : "manual completion requires all required children to be in {Disabled, Completed, Terminated, Failed}"));
            }

            await base.Trigger(transition);
        }

        // Live snapshots of every child instance this stage has created (including repetitions) -
        // the same set HandleChildTransitioned's automatic criteria evaluate, reused by the
        // manual-completion gate so both Table 8.12 paths judge the same population.
        protected async Task<PlanItemSnapshot[]> GetChildSnapshots() =>
            await Task.WhenAll(StageStore.Children
                .SelectMany(kvp => kvp.Value.Keys)
                .Select(piInstanceId => Host.GrainFactory.GetGrain<IPlanItemInternalGrain>(
                        Host.CaseInstanceId,
                        $"{Host.Address}.{piInstanceId}")
                    .GetSnapshot()));

        private async Task HandleChildRepeated(PlanItemRepetitionCriteriaMetEvent @event, StreamSequenceToken token = null)
        {
            // ignore events that are not direct children of this stage
            if (@event.SourceScope != Host.Address) return;

            var child = PlanItemDefinition.PlanItems
                .SingleOrDefault(pi => pi.Id == @event.SourceDefinitionId);

            if (child == null)
            {
                Host.LogWithContext(logger => logger.LogCritical(
                    "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | received child repeat from unknown child {ChildElementDefinitionId}",
                    Host.Definition.GetType().Name,
                    PlanItemDefinition.GetType().Name,
                    Host.Scope,
                    Host.InstanceId,
                    @event.SourceDefinitionId));
                return;
            }

            var nextRepetition = @event.CurrentRepetition + 1;

            // ADO #67 - Case.Flow ENGINE EXTENSION (RepetitionGuardOptions), NOT CMMN spec
            // surface.
            // ~~~~~
            // 8.6.4 imposes no upper bound on how many times a repeating item may re-spawn - see
            // RepetitionGuardOptions' remarks for the #19 foot-gun this closes. Faulting THIS
            // container (Host) rather than the repeating child is deliberate: the child that
            // published PlanItemRepetitionCriteriaMetEvent is either already terminal
            // (Completed/Terminated, for the no-entry-criteria path - BaseBehavior.
            // TryRepeatOnCompleteOrTerminate) or still legitimately running (the entry-criterion
            // OnPart path - HandleSentrySatisfied), and PlanItemStateMachine.
            // ConfigureForStageOrTask permits no outgoing transition at all from Completed/
            // Terminated for an ordinary Stage/Task - neither is ever a legal Fault target. This
            // Stage/CasePlanModel is the one actually issuing the runaway CreateChild calls and
            // is still Active when the ceiling is hit (Active legally Permits(Fault, Failed) in
            // both ConfigureForStageOrTask and ConfigureForCasePlanModel) - the only always-valid
            // target, and the entity actually responsible for the spawn.
            if (nextRepetition >= _repetitionCeiling)
            {
                Host.LogWithContext(logger => logger.LogError(
                    "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | repetition ceiling {Ceiling} reached for child {ChildElementDefinitionId} (refusing repetition {NextRepetition}) - this is a Case.Flow engine safety extension (#67), not CMMN 1.1 spec behavior. Faulting this container instead of spawning further instances.",
                    Host.Definition.GetType().Name,
                    PlanItemDefinition.GetType().Name,
                    Host.Scope,
                    Host.InstanceId,
                    _repetitionCeiling,
                    child.Id,
                    nextRepetition));

                Host.RaiseEvent(new RepetitionCeilingExceeded
                {
                    RepeatingPlanItemDefinitionId = child.Id,
                    AttemptedRepetition = nextRepetition,
                    Ceiling = _repetitionCeiling
                });

                if (StateMachine.CanFire(PlanItemTransition.Fault))
                {
                    // HandleTransitioned (BaseBehavior) raises Transitioned and confirms - the
                    // same ConfirmEvents() call commits the RepetitionCeilingExceeded event
                    // raised just above, per the same Publish-before-Repeated/Bug #61 ordering
                    // discipline used elsewhere in this class.
                    await StateMachine.FireAsync(PlanItemTransition.Fault);
                }
                else
                {
                    // Host cannot Fault from its current state (unexpected, but not this
                    // method's call to force) - confirm explicitly so the ceiling event is not
                    // left sitting unconfirmed in TentativeState indefinitely (Bug #61).
                    await Host.ConfirmEvents();
                }

                return;
            }

            // Bug #62 root cause (fixed, #19): this template previously declared SIX placeholders
            // but passed FIVE arguments (the long-standing CA2017 warning). Message-template
            // renderers that format eagerly (MEL's FormattedLogValues via String.Format - e.g.
            // the console logger, or Orleans.TestingHost's file logger) THROW FormatException on
            // the mismatch, MEL rethrows it as AggregateException, and since this runs inside a
            // stream-delivery turn the fault was swallowed by the streaming agent's
            // retry-then-drop - so HandleChildRepeated appeared "never reached" and repetitions
            // silently never spawned. The subscription wiring was correct the whole time.
            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | instantiating repetition {Repetition} of {ChildElementDefinitionId}",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                nextRepetition,
                child.Id));

            Host.RaiseEvent(new ChildRepeated());

            await CreateChild(child, nextRepetition);
        }

        private async Task CreateChild(Interfaces.Model.PlanItem child, int repetition = 0)
        {
            var childInstanceId = ShortGuid.NewGuid();

            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | creating instance {ChildElementInstanceId} of child {ChildElementDefinitionId}",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                childInstanceId,
                child.Id));

            var childGrain = Host.GrainFactory.GetGrain<IPlanItemInternalGrain>(
                Host.CaseInstanceId,
                $"{Host.Address}.{childInstanceId}");

            // Host.DefinitionId: this Stage's own definition id, i.e. the new child's PARENT
            // definition id - threaded through so the child's BaseBehavior.Activate can key its
            // parent-transition subscription on the same stream this Stage's transitions publish
            // on (#63).
            // Host.DefinitionScope: this Stage's own full DEFINITION-scope path, threaded through
            // so the child's CaseDefinitionGrain.GetPlanItemDefinition lookup searches from the
            // correct definition-tree position instead of this Stage's runtime instance address
            // (#65).
            await childGrain.DefineRepetition(Host.State.CaseDefinitionId, child, repetition, Host.DefinitionId, Host.DefinitionScope);
            await childGrain.Trigger(PlanItemTransition.Create);

            await Task.WhenAll(
                Host.SubscribeTo<PlanItemTransitionedEvent>(
                    child.Id,
                    HandleChildTransitioned,
                    StreamFlags.Create),
                Host.SubscribeTo<PlanItemRepetitionCriteriaMetEvent>(
                    child.Id,
                    HandleChildRepeated,
                    StreamFlags.Create));

            Host.RaiseEvent(new ChildCreated
            {
                PlanItemId = child.Id,
                PlanItemInstanceId = childInstanceId,
                Repetition = repetition
            });
        }
    }
}
