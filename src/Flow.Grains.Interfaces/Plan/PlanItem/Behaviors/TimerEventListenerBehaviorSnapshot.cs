using System;
using Orleans;

namespace Flow.Grains.Interfaces.Plan.PlanItem.Behaviors
{
    [GenerateSerializer]
    public class TimerEventListenerBehaviorSnapshot
    {
        [Id(0)]
        public DateTime? TimerStart { get; set; }

        [Id(1)]
        public Iso8601Snapshot TimerSchedule { get; set; }
        [Id(2)]
        public string TimerScheduleEvaluationError { get; set; }
    }
}
