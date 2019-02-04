using System;

namespace Flow.Grains.Plan.PlanItem.Behaviors.Stores
{
    [Serializable]
    public class TimerEventListenerBehaviorStore : BehaviorStore
    {
        public DateTime? TimerStart { get; private set; }
    }
}
