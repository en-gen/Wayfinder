using System;

namespace Flow.Grains.Plan.PlanItem.Behaviors.Stores
{
    [Serializable]
    public abstract class BehaviorStore
    {
        public TBehaviorStore As<TBehaviorStore>()
            where TBehaviorStore : BehaviorStore =>
            this is TBehaviorStore
                ? this as TBehaviorStore
                : throw new InvalidOperationException();
    }
}
