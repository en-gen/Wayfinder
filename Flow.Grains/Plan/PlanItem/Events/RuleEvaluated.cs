using Flow.Grains.Plan.CmmnElement.Events;

namespace Flow.Grains.Plan.PlanItem.Events
{
    public abstract class RuleEvaluated : BaseUpdate
    {
        public bool Result { get; set; }
        public string Error { get; set; }
    }
}