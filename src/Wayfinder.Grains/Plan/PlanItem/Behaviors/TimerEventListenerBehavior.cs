using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Expressions;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Plan.PlanItem.StateMachine;
using Wayfinder.Grains.Scheduler;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace Wayfinder.Grains.Plan.PlanItem.Behaviors
{
    public class TimerEventListenerBehavior : EventListenerBehavior<TimerEventListener>
    {
        private TimerEventListenerBehaviorStore TimerStore => Host.State.BehaviorExtension as TimerEventListenerBehaviorStore ?? throw new InvalidOperationException();

        public TimerEventListenerBehavior(IBehaviorHost host, TimerEventListener planItemDefinition, IPlanItemStateMachine stateMachine) :
            base(host, planItemDefinition, stateMachine)
        {
            StateMachine.Configure(PlanItemState.Available)
                .OnEntryFromAsync(PlanItemTransition.Create, HandleEnterAvailableFromCreate)
                // #182 (sub-claim 2) - ConfigureForMilestoneOrEventListener defines exactly one
                // outgoing Suspended trigger, Resume -> Available (no separate ParentResume
                // permit the way Stage/Task's Suspended config has - see
                // EventListenerBehavior.HandleParentTransitioned, which maps every incoming
                // Resume/ParentResume/Reactivate cascade onto this same local Resume trigger), so
                // hooking Resume here is the single choke point every real resume path funnels
                // through for this state machine shape.
                .OnEntryFromAsync(PlanItemTransition.Resume, HandleEnterAvailableFromResume);

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
                        await Host.GrainFactory.GetScheduler(Host.CaseInstanceId)
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

        // #31: this used to call GetGrain<ITimerEventSchedulerGrain>(Host.CaseInstanceId,
        // Host.InstanceId) - a 2-Guid-arg-looking call that actually resolves to
        // IGrainFactory.GetGrain<T>(Guid primaryKey, string grainClassNamePrefix = null), since
        // ITimerEventSchedulerGrain is IGrainWithGuidKey (not IGrainWithGuidCompoundKey). The second
        // argument is NOT a key extension - it's an optional grain-CLASS-name prefix used only to
        // disambiguate between multiple implementations of the same interface. Passing
        // Host.InstanceId (a plan-item short-guid) there doesn't scope anything by plan item; it
        // just fails to match TimerEventSchedulerGrain's type name, so grain-class resolution throws
        // and CancelTimer never actually ran - every TimerEventListener leaked its Quartz job/trigger
        // on Terminated. The correct (and only needed) key is the case instance id, matching the
        // other 2 call sites - CancelTimer's own planItemInstanceId parameter already gives it
        // per-plan-item granularity inside the case-scoped scheduler grain.
        private Task HandleTerminated() => Host.GrainFactory
            .GetScheduler(Host.CaseInstanceId)
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
        //
        // #79: this is a stream-subscription handler (reached via HandleStartTriggerSourceTransitioned,
        // not a state-machine transition callback), so nothing else in the pipeline confirms the
        // event it raises - the same shape of gap #61 fixed for HandleEnterAvailableFromCreate above.
        // Without an explicit confirm, TimerStartTriggerOccurred would sit queued in TentativeState
        // and be lost outright if the grain deactivates before anything else happens to confirm.
        private async Task HandleStartTriggerOccurred(DateTime occurred)
        {
            Host.LogWithContext(logger => logger.LogInformation(
                "{Element} [{PlanItemDefinition}] {ElementScope}.{ElementInstanceId} | {StartTriggerType} [{StartTriggerDefinitionId}] {StandardEvent} occurred",
                Host.Definition.GetType().Name,
                PlanItemDefinition.GetType().Name,
                Host.Scope,
                Host.InstanceId,
                PlanItemDefinition.TimerStart.GetType().Name,
                (string)((dynamic)PlanItemDefinition.TimerStart).SourceRef,
                (object)((dynamic)PlanItemDefinition.TimerStart).StandardEvent));

            Host.RaiseEvent(new TimerStartTriggerOccurred
            {
                Occurred = occurred
            });
            await Host.ConfirmEvents();

            await Host.GrainFactory.GetScheduler(Host.CaseInstanceId)
                .ScheduleTimer(Host.InstanceId, TimerStore.TimerSchedule, occurred, Host.Context);
        }

        // #182 - the live stream handler is now a thin adapter over ProcessTick, which also
        // drives the buffered-tick replay below (HandleEnterAvailableFromResume). Keeping a
        // single dispatch point means a replayed tick goes through EXACTLY the same
        // Available/Completed/Suspended branching a live tick would, including re-checking
        // current state each time (see ProcessTick's own remarks on why that matters for
        // multi-tick replay).
        private Task HandleTimerTickedEvent(TimerTickedEvent @event, StreamSequenceToken token = null) =>
            ProcessTick(@event.FireTime);

        // #182 - dispatches strictly on CURRENT state, never on how the tick arrived (live stream
        // delivery vs. replay), so a replayed tick that lands on an item no longer Available (e.g.
        // a second buffered tick from a schedule that ticked more than once while suspended, which
        // the first replayed tick already carried to Completed) is handled exactly as a live tick
        // in that same state would be - including, if some future state-machine change ever made
        // re-suspension mid-replay possible, re-buffering via the Suspended arm below rather than
        // silently dropping it.
        private async Task ProcessTick(DateTimeOffset fireTime)
        {
            switch (Host.State.PlanItemState)
            {
                case PlanItemState.Available:
                    {
                        await StateMachine.FireAsync(PlanItemTransition.Occur);
                        break;
                    }
                case PlanItemState.Completed:
                    {
                        await Host.UnsubscribeFrom<TimerTickedEvent>(Host.InstanceId);

                        // #182 (sub-claim 1) - see BaseBehavior.HasExplicitRepetitionRule's
                        // remarks. No attached rule (the common recurring-timer case) short-
                        // circuits to true and EvaluateRepetitionRule is never called - the
                        // republish below stays exactly as unconditional as it was before this
                        // fix, no new audit event, no new evaluation side effect.
                        var shouldRepeat = !HasExplicitRepetitionRule() || await EvaluateRepetitionRule();

                        if (shouldRepeat)
                        {
                            await Host.Publish(new PlanItemRepetitionCriteriaMetEvent(
                                Host.Scope,
                                Host.InstanceId,
                                Host.DefinitionId,
                                Host.State.Repetition));
                        }

                        await Host.ConfirmEvents();
                        break;
                    }
                case PlanItemState.Suspended:
                    {
                        // #182 (sub-claim 2) - buffer rather than drop; see
                        // SuspendedTimerTickBuffered's remarks and docs section 2 ("suspension
                        // preserves state; it never discards it").
                        Host.RaiseEvent(new SuspendedTimerTickBuffered { FireTime = fireTime });
                        await Host.ConfirmEvents();
                        break;
                    }
                    // Any other state (Terminated) means this tick arrived too late to matter -
                    // the listener is done and nothing here should act on it.
            }
        }

        // #182 (sub-claim 2) - runs as a state ENTRY action on Available, reached only via this
        // item's own Resume trigger (see the constructor's remarks on why Resume is the single
        // choke point). Snapshots-then-clears PendingSuspendedTicks with one RaiseEvent up front
        // (SuspendedTimerTicksReplayed) so the replay loop below iterates a local list, never the
        // live store collection the same turn could still be appending to. If this item is
        // terminated instead of resumed (Suspended -[ParentTerminate]-> Terminated is the only
        // other permitted exit from Suspended - see ConfigureForMilestoneOrEventListener), this
        // method never runs at all, so a buffered tick behind a terminated listener is correctly
        // never replayed - it simply stays inert in the now-terminal grain's state.
        private async Task HandleEnterAvailableFromResume()
        {
            var pendingTicks = TimerStore.PendingSuspendedTicks;
            if (pendingTicks.Count == 0) return;

            var snapshot = pendingTicks.Select(t => t.FireTime).ToList();

            Host.RaiseEvent(new SuspendedTimerTicksReplayed());
            await Host.ConfirmEvents();

            foreach (var fireTime in snapshot)
            {
                await ProcessTick(fireTime);
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
