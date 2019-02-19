using System;

namespace Flow.Grains.Interfaces.Plan.PlanItem.Behaviors
{
    [Serializable]
    public class TimerEventListenerBehaviorSnapshot
    {
        public DateTime? TimerStart { get; set; }

        public Iso8601Snapshot TimerSchedule { get; set; }
        public string TimerScheduleEvaluationError { get; set; }
    }
}
