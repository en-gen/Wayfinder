using Flow.Grains.Plan.CmmnElement.Events;

namespace Flow.Grains.Plan.PlanItem.Events
{
    public abstract class RuleEvaluated<TResult> : BaseUpdate
    {
        public TResult Result { get; set; }
        public string Error { get; set; }
    }
}