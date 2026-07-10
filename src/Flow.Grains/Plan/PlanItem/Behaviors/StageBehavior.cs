using System;
using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
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

        public StageBehavior(IBehaviorHost host, Stage planItemDefinition, IPlanItemStateMachine stateMachine) :
            base(host, planItemDefinition, stateMachine)
        {
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
        private Task HandleEnterAvailableFromCreate() =>
            Task.WhenAll(
                EvaluateRepetitionRule(),
                EvaluateRequiredRule(),
                // 8.7 - Stage and Task instances states
                // ~~~~~
                // While available, the Stage or Task instance is waiting for its entry criteria (Sentry) to become TRUE.
                // A missing entry criteria(Sentry) is considered TRUE.
                Host.Definition.EntryCriteria.Any()
                    ? SubscribeToCriteria(x => x.EntryCriteria, StreamFlags.Create)
                    : EnableOrStart(),
                SubscribeToCriteria(x => x.ExitCriteria, StreamFlags.Create));

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
                     StateMachine.CanFire(PlanItemTransition.Exit))
            {
                Host.RaiseEvent(new ExitCriterionSatisfied
                {
                    SourceScope = @event.SourceScope,
                    SourceId = @event.SourceDefinitionId,
                    OnPartOccurred = @event.OnPartOccurred
                });

                await StateMachine.FireAsync(PlanItemTransition.Exit);
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

            var childSnapshots = await Task.WhenAll(StageStore.Children
                .SelectMany(kvp => kvp.Value.Keys)
                .Select(piInstanceId => Host.GrainFactory.GetGrain<IPlanItemInternalGrain>(
                        Host.CaseInstanceId,
                        $"{Host.Address}.{piInstanceId}")
                    .GetSnapshot()));


            if (!Host.State.UserCompletable &&
                StateMachine.CanFire(PlanItemTransition.Complete) &&
                childSnapshots.All(x => x.PlanItemState != PlanItemState.Active) &&
                childSnapshots.Where(x => x.Required).All(x => x.PlanItemState.IsTerminal()))
            {
                Host.RaiseEvent(new UserCompletableCriteriaMet());
            }

            // 8.6.1 - Stage.autoComplete
            // ~~~~~
            // autoComplete = TRUE
            // There are no Active children, AND all required (requiredRule evaluates to TRUE) children are
            // in {Disabled, Completed, Terminated, Failed}.
            //
            // autoComplete = FALSE
            // There are no Active children AND (all children are in {Disabled, Completed, Terminated, Failed}
            // AND there are no DiscretionaryItems) OR (Manual Completion AND all required (requiredRule evaluates
            // to TRUE) children are in { Disabled, Completed, Terminated, Failed}).
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
                // ...There are no Active Children AND all children are in {Disabled, Completed, Terminated, Failed}
                else
                {
                    if (childSnapshots.All(x => x.PlanItemState.IsTerminal()) &&
                        // ...there are no (unplanned) DiscretionaryItems
                        (PlanItemDefinition.PlanningTable == null ||
                         Host.State.UserCompletable &&
                         PlanItemDefinition.PlanningTable.DiscretionaryItems
                             .All(di => childSnapshots.Any(snap => snap.Definition.Id == di.Id))) &&
                        StateMachine.CanFire(PlanItemTransition.Complete))
                    {
                        Host.RaiseEvent(new FullyCompleteCriteriaMet());
                        await StateMachine.FireAsync(PlanItemTransition.Complete);
                    }
                }
            }
        }

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

            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | instantiating repetition {ChildElementInstanceId} of {ChildElementDefinitionId}",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                child.Id));

            Host.RaiseEvent(new ChildRepeated());

            await CreateChild(child, @event.CurrentRepetition + 1);
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

            await childGrain.DefineRepetition(Host.State.CaseDefinitionId, child, repetition);
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
                PlanItemDefinitionId = child.Id,
                PlanItemInstanceId = childInstanceId,
                Repetition = repetition
            });
        }
    }
}
