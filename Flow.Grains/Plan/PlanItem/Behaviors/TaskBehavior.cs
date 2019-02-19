using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public class TaskBehavior<TTask> :
        BaseBehavior<TTask>
        where TTask : BaseTask
    {
        public TaskBehavior(IBehaviorHost host, TTask planItemDefinition, IPlanItemStateMachine stateMachine) :
            base(host, planItemDefinition, stateMachine)
        {
            // 8.7 - Stage and Task instance states
            // ~~~~~
            // A Stage or Task instance becomes available when
            //
            //  • The Stage instance in which it resides moves into Active state.
            //    or
            //  • The Stage or Task has a Sentry with an OnPart that has a sourceRef outside of the enclosing
            //    Stage of that Stage or Task and this Sentry is satisfied. In that case, the Stage instance and
            //    recursively all Stage instances up to the enclosing Stage of the Stage or Task in which the
            //    Stage or Task resides moves into Active state if not already active.
            //
            // While available, the Stage or Task instance is waiting for its entry criteria (Sentry) to become TRUE.
            // A missing entry criteria (Sentry) is considered TRUE.
            StateMachine.Configure(PlanItemState.Available)
                .OnEntryFromAsync(PlanItemTransition.Create, HandleEnterAvailableFromCreate);

            // 8.7 - Stage and Task instance states
            // ~~~~~
            // The Stage or Task considered instance is executing in this state. Stage instances in this state contain at
            // least one Stage or Task instance in the Available, Enabled, Active, Suspended state, or autoComplete is
            // set to FALSE
            StateMachine.Configure(PlanItemState.Active)
                .OnEntryAsync(HandleEnterActive);
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
        // 5.24 - PlanItemAttributes
        // exitCriteria: A PlanItem that is defined by a Task that is non-blocking (isBlocking set
        // to FALSE) MUST NOT have exitCriteriaRefs.
        private Task HandleEnterAvailableFromCreate() =>
            Task.WhenAll(
                EvaluateRepetitionRule(),
                EvaluateRequiredRule(),
                // 8.7 - Stage and Task instances states
                // ~~~~~
                // While available, the Stage or Task instance is waiting for its entry criteria (Sentry) to become TRUE.
                // A missing entry criteria(Sentry) is considered TRUE.
                Host.Definition.EntryCriteriaSpecified
                    ? SubscribeToCriteria(x => x.EntryCriteria, StreamFlags.Create)
                    : Task.Factory.StartNew(async () =>
                    {
                        if (await EvaluateManualActivationRule())
                        {
                            await StateMachine.FireAsync(PlanItemTransition.Enable);
                        }
                        else
                        {
                            await StateMachine.FireAsync(PlanItemTransition.Start);
                        }
                    }),
                PlanItemDefinition.IsBlocking
                    ? SubscribeToCriteria(x => x.ExitCriteria, StreamFlags.Create)
                    : Task.CompletedTask);

        // Table 5.39 - Task attributes and model associations
        // ~~~~~
        // If isBlocking is set to TRUE, the Task is waiting until the work associated with
        // the Task is completed.If isBlocking is set to FALSE, the Task is not waiting for
        // the work to complete and completes immediately, upon instantiation.
        //
        // The default value of attribute isBlocking MUST be TRUE.
        //
        // A Task that is non-blocking (isBlocking set to FALSE) MUST NOT have
        // outputs.
        //
        // NOTE: I am interpreting "upon instantiation" as when the task becomes Active after being
        //       started, not Available after being "created". To do so would have negative side
        //       effects for repeatable tasks and would make manual activation rules irrelevant
        private async Task HandleEnterActive()
        {
            if (!PlanItemDefinition.IsBlocking)
            {
                Host.LogWithContext(logger => logger.LogInformation(
                    "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | non-blocking task Activated, transitioning directly to Complete",
                    Host.Definition.GetType().Name,
                    PlanItemDefinition.GetType().Name,
                    Host.Scope,
                    Host.InstanceId));

                await StateMachine.FireAsync(PlanItemTransition.Complete);
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

                if (@event.OnPartOccurred && StateMachine.IsInState(PlanItemState.Uninitialized))
                {
                    await StateMachine.FireAsync(PlanItemTransition.Create);
                }

                if (StateMachine.IsInState(PlanItemState.Available))
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
    }
}
