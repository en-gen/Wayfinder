using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Flow.Grains.Plan.PlanningTable;
using Flow.Grains.Plan.Sentry;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    // TODO: subscribe to PlanItem ExitCriteria (when?)
    // TODO: subscribe to PlanItemDefinition ExitCriteria (when?)

    public class StageBehavior : BaseBehavior<Stage>
    {
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
                            "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId}: creating sentry {SentryId}",
                            Host.Definition.GetType().Name,
                            PlanItemDefinition.GetType().Name,
                            Host.Scope,
                            Host.InstanceId,
                            sentryInstanceId));

                        await sentryGrain.Define(Host.State.CaseDefinitionId, sentry);

                        // TODO: should raise an event to catalog sentry instance?
                    })),
                // define planning table
                Task.Factory.StartNew(async () =>
                {
                    if (PlanItemDefinition.PlanningTable == null) return;

                    var planningTableGrain = Host.GrainFactory
                        .GetGrain<IPlanningTableGrain>(Host.CaseInstanceId, Host.InstanceId);

                    if (await planningTableGrain.Defined()) return;

                    await planningTableGrain.Define(Host.State.CaseDefinitionId, PlanItemDefinition.PlanningTable);
                }));

        // 8.8 - Stage and Task instance transitions
        // ~~~~~
        // Create: Transition to the initial state (Available) when the Stage or Task
        // instance is created. This happens when the Stage instance
        // containing this Stage or Task instance transitions to Active. The
        // RepetitionRule Boolean expression MUST be evaluated in this
        // transition. The RequiredRule Boolean expression MUST be
        // evaluated in this transition, and its Boolean value SHOULD be
        // maintained for the rest of the life of the Stage or Task instance.
        private Task HandleEnterAvailableFromCreate() =>
            Task.WhenAll(
                EvaluateRepetitionRule(),
                EvaluateRequiredRule(),
                SubscribeToCriteria(x => x.ExitCriteria, StreamFlags.Create));

        private Task HandleEnterActiveFromStart() =>
            Task.WhenAll(PlanItemDefinition.PlanItems
                .Select(x => CreateChild(x)));

        // TODO - should be raising EntryCriterionSatisfied and ExitCriterionSatisfied
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
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId}: {CriterionType} {CriterionId} satisfied by sentry {SentryRef}.  OnPart: {OnPartOccurred}",
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
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId}: processing instance {ChildElementInstanceId} of child {ChildElementDefinitionId} transition {PreviousState} × {StandardEvent} = {CurrentState}",
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
                else
                {
                    if (Host.State.PlanItemState == PlanItemState.Disabled)
                    {
                        await StateMachine.FireAsync(PlanItemTransition.Reenable);
                    }
                    if (Host.State.PlanItemState == PlanItemState.Enabled)
                    {
                        await StateMachine.FireAsync(PlanItemTransition.ManualStart);
                    }
                }
            }

            var childSnapshots = await Task.WhenAll(Host.State.Children
                .SelectMany(kvp => kvp.Value.Keys)
                .Select(piInstanceId => Host.GrainFactory.GetGrain<IPlanItemGrain>(
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
                    "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId}: received child repeat from unknown child {ChildElementDefinitionId}",
                    Host.Definition.GetType().Name,
                    PlanItemDefinition.GetType().Name,
                    Host.Scope,
                    Host.InstanceId,
                    @event.SourceDefinitionId));
                return;
            }

            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId}: instantiating repetition {ChildElementInstanceId} of {ChildElementDefinitionId}",
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
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId}: creating instance {ChildElementInstanceId} of child {ChildElementDefinitionId}",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                childInstanceId,
                child.Id));

            var childGrain = Host.GrainFactory.GetGrain<IPlanItemGrain>(
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
