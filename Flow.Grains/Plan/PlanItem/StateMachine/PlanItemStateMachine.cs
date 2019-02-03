using Flow.Grains.Interfaces.Model;
using Microsoft.Extensions.Logging;
using Stateless;

namespace Flow.Grains.Plan.PlanItem.StateMachine
{
    public class PlanItemStateMachine : StateMachine<PlanItemState, PlanItemTransition>, IPlanItemStateMachine
    {
        public PlanItemState? ParentSuspendState { get; private set; }

        private ILogger Logger { get; }

        public PlanItemStateMachine(PlanItemStore planItemStore, ILogger<PlanItemStateMachine> logger) :
            base(planItemStore.PlanItemState)
        {
            Logger = logger;

            ParentSuspendState = planItemStore.ParentSuspendState;

            ConfigureFor(planItemStore.PlanItemDefinition);
        }

        private void ConfigureFor(PlanItemDefinition planItemDefinition)
        {
            Logger.LogInformation("Initializing {BehaviorType} state machine", planItemDefinition.GetType().Name);

            Configure(PlanItemState.Uninitialized)
                .Permit(PlanItemTransition.Create, PlanItemState.Available);

            switch (planItemDefinition)
            {
                case Stage stage:
                case BaseTask task:
                {
                    ConfigureForStageOrTask();
                    break;
                }
                case Milestone milestone:
                case EventListener eventListener:
                {
                    ConfigureForMilestoneOrEventListener();
                    break;
                }
            }
        }

        private void ConfigureForStageOrTask()
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
            Configure(PlanItemState.Available)
                .Permit(PlanItemTransition.Enable, PlanItemState.Enabled)
                .Permit(PlanItemTransition.Start, PlanItemState.Active)
                .Permit(PlanItemTransition.Exit, PlanItemState.Terminated)
                .Permit(PlanItemTransition.ParentSuspend, PlanItemState.Suspended);

            // 8.7 - Stage and Task instance states
            // ~~~~~
            // A Stage or Task instance in this state is waiting for a human to start or disable it. Only Stage or Task
            // instances that require Case worker(human) intervention to start get into this state
            // (ManualActivationRule evaluates to TRUE).
            Configure(PlanItemState.Enabled)
                .Permit(PlanItemTransition.Disable, PlanItemState.Disabled)
                .Permit(PlanItemTransition.ManualStart, PlanItemState.Active)
                .Permit(PlanItemTransition.Exit, PlanItemState.Terminated)
                .Permit(PlanItemTransition.ParentSuspend, PlanItemState.Suspended);

            // 8.7 - Stage and Task instance states
            // ~~~~~
            // Semi-terminal state. This state is reached when a Case worker (human) decides the Stage or Task
            // instance should not execute in this instance of the Case.
            Configure(PlanItemState.Disabled)
                .Permit(PlanItemTransition.Reenable, PlanItemState.Enabled)
                .Permit(PlanItemTransition.Exit, PlanItemState.Terminated)
                .Permit(PlanItemTransition.ParentSuspend, PlanItemState.Suspended);

            // 8.7 - Stage and Task instance states
            // ~~~~~
            // The Stage or Task considered instance is executing in this state. Stage instances in this state contain at
            // least one Stage or Task instance in the Available, Enabled, Active, Suspended state, or autoComplete is
            // set to FALSE
            Configure(PlanItemState.Active)
                .Permit(PlanItemTransition.Suspend, PlanItemState.Suspended)
                .Permit(PlanItemTransition.Fault, PlanItemState.Failed)
                .Permit(PlanItemTransition.Complete, PlanItemState.Completed)
                .Permit(PlanItemTransition.Terminate, PlanItemState.Terminated)
                .Permit(PlanItemTransition.Exit, PlanItemState.Terminated)
                .Permit(PlanItemTransition.ParentSuspend, PlanItemState.Suspended);

            // 8.7 - Stage and Task instance states
            // ~~~~~
            // This state allows a Case worker (human) to temporarily suspend an executing Stage or Task instance.
            // A Stage instance MUST propagate this state to all its contained EventListener, Milestone, Stage,
            // and Task instances
            Configure(PlanItemState.Suspended)
                .Permit(PlanItemTransition.Resume, PlanItemState.Active)
                .Permit(PlanItemTransition.Exit, PlanItemState.Terminated)
                .PermitDynamicIf(PlanItemTransition.ParentResume,
                    () => ParentSuspendState.Value,
                    () => ParentSuspendState.HasValue)
                .OnEntryFrom(PlanItemTransition.ParentSuspend, transition => ParentSuspendState = transition.Source)
                .OnExit(() => ParentSuspendState = null);

            // 8.7 - Stage and Task instance states
            // ~~~~~
            // Semi-terminal state. This state indicates an exception or software failure.
            Configure(PlanItemState.Failed)
                .Permit(PlanItemTransition.Reactivate, PlanItemState.Active)
                .Permit(PlanItemTransition.Exit, PlanItemState.Terminated);
        }

        private void ConfigureForMilestoneOrEventListener()
        {
            // 8.10 - EventListener and Milestone instance states
            // ~~~~~
            // In this state an EventListener instance is waiting for the event to occur. A Milestone instance in this
            // state is waiting for the Sentry (as entry criterion) to be satisfied.
            Configure(PlanItemState.Available)
                .Permit(PlanItemTransition.Suspend, PlanItemState.Suspended)
                .Permit(PlanItemTransition.Terminate, PlanItemState.Terminated)
                .Permit(PlanItemTransition.Occur, PlanItemState.Completed)
                .Permit(PlanItemTransition.ParentTerminate, PlanItemState.Terminated);

            // 8.10 - EventListener and Milestone instance states
            // ~~~~~
            // This state allows a Case worker(human) or an enclosing Stage instance to temporarily suspend an
            // EventListener instance for which the event has not yet occurred, or to suspend a Milestone instance
            // that has not been reached.
            Configure(PlanItemState.Suspended)
                .Permit(PlanItemTransition.Resume, PlanItemState.Available)
                .Permit(PlanItemTransition.ParentTerminate, PlanItemState.Terminated);
        }
    }
}
