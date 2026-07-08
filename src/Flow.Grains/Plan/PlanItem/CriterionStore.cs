using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Events;
using Orleans;

namespace Flow.Grains.Plan.PlanItem
{
    [GenerateSerializer]
    public class CriterionStore
    {
        [Id(0)]
        public string SatisfiedByAddress { get; private set; }
        [Id(1)]
        public CriterionState State { get; private set; }

        public void Apply(CriterionSatisfied @event)
        {
            SatisfiedByAddress = $"{@event.SourceScope}.{@event.SourceId}";
            State = CriterionState.Satisfied;
            if (@event.OnPartOccurred)
            {
                State |= CriterionState.OnPartOccurred;
            }
        }
    }
}