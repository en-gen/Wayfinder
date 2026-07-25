using System;
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

        public void Apply(TimerStartTriggerOccurred @event)
        {
            TimerStart = @event.Occurred;
        }

        public void Apply(TimerExpressionEvaluated @event)
        {
            TimerSchedule = @event.Result;
            TimerScheduleEvaluationError = @event.Error;
        }
    }
}
