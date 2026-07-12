using System;
using Flow.Grains.Interfaces.Model;
using Microsoft.Extensions.Logging;
using Stateless;

namespace Flow.Grains.Plan.PlanItem.StateMachine
{
    public class PlanItemStateMachine : StateMachine<PlanItemState, PlanItemTransition>, IPlanItemStateMachine
    {
        public PlanItemState? ParentSuspendState { get; private set; }

        private readonly ILogger _logger;

        public PlanItemStateMachine(IBehaviorStore planItemStore, ILogger<PlanItemStateMachine> logger) :
            base(planItemStore.PlanItemState)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            ParentSuspendState = planItemStore.ParentSuspendState;

            // #64 - Orleans activation-context safety for reentrant/queued Fire calls
            // ~~~~~
            // base(TState) constructs Stateless in FiringMode.Queued (its own default - see
            // Stateless.StateMachine<TState,TTrigger>'s single-TState ctor), which is what lets
            // StageBehavior/TaskBehavior.EnableOrStart call StateMachine.FireAsync(Start)
            // REENTRANTLY from inside HandleEnterAvailableFromCreate (itself an OnEntryFromAsync
            // callback of the in-flight Create transition): Stateless notices _firing is already
            // true, enqueues Start, and drains it later in the SAME InternalFireQueuedAsync call
            // once Create finishes - by design, not a new grain turn.
            //
            // The bug was WHERE that later draining resumes. Stateless's own
            // RetainSynchronizationContext flag (XML doc: "For certain situations, it is
            // essential that the SynchronizationContext is retained for all delegate calls.")
            // defaults to false, and every internal await Stateless takes between firing Create
            // and draining the queued Start - InternalFireQueuedAsync's own queue loop
            // (StateMachine.Async.cs), plus every OnEntryFromAsync/OnTransitionedAsync dispatch
            // in StateRepresentation.Async.cs/OnTransitionedEvent.cs - is wrapped in
            // .ConfigureAwait(RetainSynchronizationContext), i.e. ConfigureAwait(false) at that
            // default. Orleans grain code has no ambient SynchronizationContext; it relies on
            // TaskScheduler.Current (the activation's single-threaded ActivationTaskScheduler,
            // captured by the runtime for the duration of this grain turn) to bring every
            // unconfigured `await` continuation back to the activation thread - the same
            // mechanism that makes ordinary Case.Flow grain code (which never uses
            // ConfigureAwait(false)) safe. ConfigureAwait(false) opts OUT of that capture:
            // once ANY awaited step between the reentrant FireAsync(Start) call and the later
            // dequeue-and-run of that Start trigger genuinely suspends (Host.ConfirmEvents,
            // stream subscriptions, and the ManualActivationRule's IExpressionGrain call all do -
            // see HandleEnterAvailableFromCreate/EnableOrStart), its continuation - including the
            // eventual StageBehavior.HandleEnterActiveFromStart -> CreateChild ->
            // Host.GrainFactory access - resumes on whatever ThreadPool thread completed that
            // await, off the activation context entirely. Orleans then throws "Activation access
            // violation" the moment GrainFactory is touched (confirmed RED: this exact stack, via
            // the un-quarantined KnownGapScenarios.StageAutoStart__… scenario, before this fix).
            // The manual-start route (LifecycleScenarios...ManualStartInstantiatesChildren) never
            // hit this: ManualStart's entry action runs synchronously in the SAME outer FireAsync
            // call that a Case worker/test invoked directly - nothing has suspended-and-resumed
            // yet by the time CreateChild's GrainFactory access happens, so no context to lose,
            // regardless of ConfigureAwait. Only a REENTRANT, QUEUED trigger - drained after
            // real awaits have already come and gone through Stateless's own
            // ConfigureAwait(false) hops - is exposed.
            //
            // Fix: opt in to Stateless's own escape hatch instead of restructuring the
            // Create->Start cascade. RetainSynchronizationContext = true flips every one of those
            // internal awaits to ConfigureAwait(true) - ordinary, un-configured await semantics -
            // so Stateless's continuations behave exactly like the rest of this grain's code and
            // correctly resume on TaskScheduler.Current (the activation scheduler). This changes
            // WHERE continuations resume, not WHEN Start fires or WHAT fires it: the reentrant
            // FireAsync(Start) is still enqueued and drained within the SAME FireAsync(Create)
            // call, on the SAME grain turn, with no new grain-to-grain call, no
            // RegisterTimer/reminder, no fire-and-forget, and no additional reentrancy - so it
            // cannot deadlock (still pure async/await, nothing blocks on .Result/.Wait()) and
            // cannot loop unboundedly (Stateless's queue is bounded by the finite triggers
            // actually fired, unchanged by this flag).
            RetainSynchronizationContext = true;

            ConfigureFor(planItemStore.PlanItemDefinition);
        }

        private void ConfigureFor(PlanItemDefinition planItemDefinition)
        {
            _logger.LogInformation("Initializing {BehaviorType} state machine", planItemDefinition.GetType().Name);
            
            switch (planItemDefinition)
            {
                case Stage stage when stage.IsCasePlanModel:
                {
                    ConfigureForCasePlanModel();
                    break;
                }
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

        private void ConfigureForCasePlanModel()
        {
            Configure(PlanItemState.Uninitialized)
                .Permit(PlanItemTransition.Create, PlanItemState.Active);

            // 8.5 - Case instance states
            // ~~~~~
            // In this state the Case instance is executing; meaning the outermost Stage instance is in the Active state
            Configure(PlanItemState.Active)
                .Permit(PlanItemTransition.Complete, PlanItemState.Completed)
                .Permit(PlanItemTransition.Terminate, PlanItemState.Terminated)
                .Permit(PlanItemTransition.Fault, PlanItemState.Failed)
                .Permit(PlanItemTransition.Suspend, PlanItemState.Suspended);

            // 8.5 - Case instance states
            // ~~~~~
            // The Case instance is completed, when all the required Milestone, Stage, and Task instances in the
            // outermost Stage instance are completed(completed or terminated), and there are no executing(Active)
            // Stage or Task instances.
            Configure(PlanItemState.Completed)
                .Permit(PlanItemTransition.Reactivate, PlanItemState.Active)
                .Permit(PlanItemTransition.Close, PlanItemState.Closed);

            // 8.5 - Case instance states
            // ~~~~~
            // Terminal state. This state can be achieved by an exit criteria and also allows a Case worker (human) to
            // terminate an executing Case instance.This state is reached when the outermost Stage instance reaches it.
            Configure(PlanItemState.Terminated)
                .Permit(PlanItemTransition.Reactivate, PlanItemState.Active)
                .Permit(PlanItemTransition.Close, PlanItemState.Closed);

            // 8.5 - Case instance states
            // ~~~~~
            // Semi-terminal state. This state is reached when the outermost Stage instance reaches it. The state
            // indicates an exception or software failure.
            Configure(PlanItemState.Failed)
                .Permit(PlanItemTransition.Reactivate, PlanItemState.Active)
                .Permit(PlanItemTransition.Close, PlanItemState.Closed);

            // 8.5 - Case instance states
            // ~~~~~
            // This state allows a Case worker (human) to temporarily suspend an executing Case instance. A Case
            // instance MUST propagate this state to its outermost Stage instance. This state MUST then be
            // propagated down to the outermost Stage instance’s contained EventListener, Milestone, Stage,
            // and Task instances.
            Configure(PlanItemState.Suspended)
                .Permit(PlanItemTransition.Reactivate, PlanItemState.Active)
                .Permit(PlanItemTransition.Close, PlanItemState.Closed);
        }

        private void ConfigureForStageOrTask()
        {
            Configure(PlanItemState.Uninitialized)
                .Permit(PlanItemTransition.Create, PlanItemState.Available);

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
            Configure(PlanItemState.Uninitialized)
                .Permit(PlanItemTransition.Create, PlanItemState.Available);

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
