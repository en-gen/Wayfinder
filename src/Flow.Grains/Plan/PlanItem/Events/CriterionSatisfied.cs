using Flow.Grains.Plan.CmmnElement.Events;

namespace Flow.Grains.Plan.PlanItem.Events
{
    public abstract class CriterionSatisfied : BaseUpdate
    {
        public string SourceScope { get; set; }
        public string SourceId { get; set; }
        public bool OnPartOccurred { get; set; }
    }
}
