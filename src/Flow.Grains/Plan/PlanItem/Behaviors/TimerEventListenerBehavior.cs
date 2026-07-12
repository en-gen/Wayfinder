using System;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Executables;
using Flow.Grains.Expressions;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Behaviors.Stores;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Plan.PlanItem.StateMachine;
using Flow.Grains.Scheduler;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    public class TimerEventListenerBehavior : EventListenerBehavior<TimerEventListener>
    {
        private TimerEventListenerBehaviorStore TimerStore => Host.State.BehaviorExtension as TimerEventListenerBehaviorStore ?? throw new InvalidOperationException();

        public TimerEventListenerBehavior(IBehaviorHost host, TimerEventListener planItemDefinition, IPlanItemStateMachine stateMachine) :
            base(host, planItemDefinition, stateMachine)
        {
            StateMachine.Configure(PlanItemState.Available)
                .OnEntryFromAsync(PlanItemTransition.Create, HandleEnterAvailableFromCreate);

            StateMachine.Configure(PlanItemState.Terminated)
                .OnEntryAsync(HandleTerminated);
        }

        public override Task Activate() =>
            Task.WhenAll(
                base.Activate(),
                Host.SubscribeTo<TimerTickedEvent>(Host.InstanceId, HandleTimerTickedEvent, StreamFlags.Resume),
                ResumeStartTriggerSubscription());

        private Task ResumeStartTriggerSubscription()
        {
            switch (PlanItemDefinition.TimerStart)
            {
                case PlanItemStartTrigger piStartTrigger:
                {
                    return Host.SubscribeTo<PlanItemTransitionedEvent>(
                        piStartTrigger.SourceRef,
                        HandleStartTriggerSourceTransitioned,
                        StreamFlags.Resume);
                }
                case CaseFileItemStartTrigger cfiStartTrigger:
                {
                    return Host.SubscribeTo<CaseFileItemTransitionedEvent>(
                        cfiStartTrigger.SourceRef,
                        HandleStartTriggerSourceTransitioned,
                        StreamFlags.Resume);
                }
                default:
                {
                    return Task.CompletedTask;
                }
            }
        }
        
        // if we have a start trigger, subscribe
        // if we have a start date (from expression) schedule start
        // if we have both, error
        //
        // #61 discipline: this method runs as a state ENTRY action (OnEntryFromAsync(Create, ...)
        // on Available) - i.e. AFTER HandleTransitioned has already raised-and-confirmed the
        // Transitioned event for this Create transition (Stateless invokes the transition
        // callback before the destination state's entry actions - see
        // BaseBehavior.TryRepeatOnCompleteOrTerminate's remarks). EvaluateTimerExpression below
        // raises TimerExpressionEvaluated with no confirm of its own. Unlike StageBehavior/
        // TaskBehavior's sibling HandleEnterAvailableFromCreate (whose no-entry-criteria branch
        // falls through to EnableOrStart's nested FireAsync(Enable/Start), and that transition's
        // own HandleTransitioned confirm flushes everything queued so far), neither of this
        // method's own Fault-firing branches below is ever reachable for a TimerEventListener:
        // ConfigureForMilestoneOrEventListener - the state-machine shape every EventListener
        // (TimerEventListener included) is configured with - defines no Fault permit at all, so
        // StateMachine.CanFire(PlanItemTransition.Fault) is always false here. Without an
        // explicit confirm, the raise would sit queued in TentativeState indefinitely - lost
        // outright if the grain deactivates before anything else happens to confirm.
        private async Task HandleEnterAvailableFromCreate()
        {
            // evaluate timer expression to build schedule
            await EvaluateTimerExpression();
            await Host.ConfirmEvents();

            var schedule = TimerStore.TimerSchedule;

            if (schedule?.Start != null && PlanItemDefinition.TimerStart != null)
            {
                Host.LogWithContext(logger => logger.LogError(
                    "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | Conflicting timer starts defined: {@TimerStart} ⮿ {@TimerExpression}",
                    Host.Definition.GetType().Name,
                    PlanItemDefinition.GetType().Name,
                    Host.Scope,
                    Host.InstanceId,
                    PlanItemDefinition.TimerStart,
                    schedule));

                if (StateMachine.CanFire(PlanItemTransition.Fault))
                {
                    await StateMachine.FireAsync(PlanItemTransition.Fault);
                }

                return;
            }

            switch (PlanItemDefinition.TimerStart)
            {
                case null:
                {
                    // no deferred start trigger, so schedule immediately
                    await Host.SubscribeTo<TimerTickedEvent>(
                        Host.InstanceId,
                        HandleTimerTickedEvent,
                        StreamFlags.Create);
                    await Host.GrainFactory.GetGrain<ITimerEventSchedulerGrain>(Host.CaseInstanceId)
                        .ScheduleTimer(Host.InstanceId, TimerStore.TimerSchedule, null, Host.Context);
                    break;
                }
                case PlanItemStartTrigger piStartTrigger:
                {
                    await Host.SubscribeTo<PlanItemTransitionedEvent>(
                        piStartTrigger.SourceRef,
                        HandleStartTriggerSourceTransitioned,
                        StreamFlags.Create);
                    break;
                }
                case CaseFileItemStartTrigger cfiStartTrigger:
                {
                    await Host.SubscribeTo<CaseFileItemTransitionedEvent>(
                        cfiStartTrigger.SourceRef,
                        HandleStartTriggerSourceTransitioned,
                        StreamFlags.Create);
                    break;
                }
            }
        }

        private Task HandleTerminated() => Host.GrainFactory
            .GetGrain<ITimerEventSchedulerGrain>(Host.CaseInstanceId, Host.InstanceId)
            .CancelTimer(Host.InstanceId);

        private Task HandleStartTriggerSourceTransitioned(PlanItemTransitionedEvent @event,
            StreamSequenceToken token = null) =>
            Host.Scope.StartsWith(@event.SourceScope) &&
            (PlanItemDefinition.TimerStart is PlanItemStartTrigger piStartTrigger &&
             piStartTrigger.StandardEvent == @event.StandardEvent)
                ? HandleStartTriggerOccurred(@event.Occurred)
                : Task.CompletedTask;

        private Task HandleStartTriggerSourceTransitioned(CaseFileItemTransitionedEvent @event, StreamSequenceToken token = null) =>
            PlanItemDefinition.TimerStart is CaseFileItemStartTrigger cfiStartTrigger &&
            cfiStartTrigger.StandardEvent == @event.StandardEvent
                ? HandleStartTriggerOccurred(@event.Occurred)
                : Task.CompletedTask;

        // Table 5.19 - TimerEventListener attributes
        // ~~~~~
        // The starting trigger of the TimerEventListener. This attribute is optional. If
        // timerStart is specified, then at runtime, if the trigger occurs the time of occurrence of
        // the trigger is captured and the timerExpression SHOULD be relative to the timestamp
        // captured when the timerStart trigger occurs.
        private async Task HandleStartTriggerOccurred(DateTime occurred)
        {
            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | {StartTriggerType} [{StartTriggerDefinitionId}] {StandardEvent} occurred",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                PlanItemDefinition.TimerStart.GetType().Name,
                (string)((dynamic) PlanItemDefinition.TimerStart).SourceRef,
                (object)((dynamic) PlanItemDefinition.TimerStart).StandardEvent));

            Host.RaiseEvent(new TimerStartTriggerOccurred
            {
                Occurred = occurred
            });

            await Host.GrainFactory.GetGrain<ITimerEventSchedulerGrain>(Host.CaseInstanceId)
                .ScheduleTimer(Host.InstanceId, TimerStore.TimerSchedule, occurred, Host.Context);
        }

        private async Task HandleTimerTickedEvent(TimerTickedEvent @event, StreamSequenceToken token = null)
        {
            if (Host.State.PlanItemState == PlanItemState.Available)
            {
                await StateMachine.FireAsync(PlanItemTransition.Occur);
            }
            else if (Host.State.PlanItemState == PlanItemState.Completed)
            {
                await Host.UnsubscribeFrom<TimerTickedEvent>(Host.InstanceId);
                await Host.Publish(new PlanItemRepetitionCriteriaMetEvent(
                    Host.Scope,
                    Host.InstanceId,
                    Host.DefinitionId,
                    Host.State.Repetition));
            }
        }

        private async Task<Iso8601> EvaluateTimerExpression()
        {
            if (PlanItemDefinition.TimerExpression == null) return null;
            
            var timerExpressionResult = await Host.GrainFactory.GetGrain<IExpressionGrain>(Host.CaseInstanceId)
                .ExecuteAsIso8601(null, PlanItemDefinition.TimerExpression);

            Host.RaiseEvent(new TimerExpressionEvaluated
            {
                Result = timerExpressionResult.Value,
                Error = timerExpressionResult.Message
            });

            if ((timerExpressionResult?.IsError ?? false) &&
                StateMachine.CanFire(PlanItemTransition.Fault))
            {
                await StateMachine.FireAsync(PlanItemTransition.Fault);
            }

            return timerExpressionResult.Value;
        }
    }
}
