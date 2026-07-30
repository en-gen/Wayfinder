using System;
using System.Collections.Generic;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores
{
    [GenerateSerializer]
    public class TimerEventListenerBehaviorStore
    {
        [Id(0)]
        public DateTime? TimerStart { get; private set; }

        [Id(1)]
        public Iso8601 TimerSchedule { get; private set; }
        [Id(2)]
        public string TimerScheduleEvaluationError { get; private set; }

        // #182 (sub-claim 2) - ticks witnessed while Suspended, durably buffered for
        // TimerEventListenerBehavior.HandleEnterAvailableFromResume to replay. Growth is bounded
        // by real elapsed wall-clock time under suspension (how many Quartz fires actually land
        // while this instance stays Suspended) rather than by anything synthetic - same accepted
        // growth shape as StageBehaviorStore's own _repetitionSourceInstanceIds (see its remarks).
        [Id(3)]
        public IList<SuspendedTimerTickBuffered> PendingSuspendedTicks { get; } = new List<SuspendedTimerTickBuffered>();

        public void Apply(TimerStartTriggerOccurred @event)
        {
            TimerStart = @event.Occurred;
        }

        public void Apply(TimerExpressionEvaluated @event)
        {
            TimerSchedule = @event.Result;
            TimerScheduleEvaluationError = @event.Error;
        }

        public void Apply(SuspendedTimerTickBuffered @event)
        {
            PendingSuspendedTicks.Add(@event);
        }

        public void Apply(SuspendedTimerTicksReplayed @event)
        {
            PendingSuspendedTicks.Clear();
        }
    }
}
