using Flow.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public abstract class CriterionSatisfied : BaseUpdate
    {
        [Id(0)]
        public string SourceScope { get; set; }
        [Id(1)]
        public string SourceId { get; set; }
        [Id(2)]
        public bool OnPartOccurred { get; set; }
    }
}
