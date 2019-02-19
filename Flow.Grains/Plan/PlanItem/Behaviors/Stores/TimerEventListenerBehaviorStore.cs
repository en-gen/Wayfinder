using System;
using Flow.Grains.Executables;
using Flow.Grains.Plan.PlanItem.Events;

namespace Flow.Grains.Plan.PlanItem.Behaviors.Stores
{
    [Serializable]
    public class TimerEventListenerBehaviorStore
    {
        public DateTime? TimerStart { get; private set; }

        public Iso8601 TimerSchedule { get; private set; }
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
