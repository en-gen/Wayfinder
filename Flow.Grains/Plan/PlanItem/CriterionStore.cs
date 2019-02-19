using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Events;

namespace Flow.Grains.Plan.PlanItem
{
    public class CriterionStore
    {
        public string SatisfiedByAddress { get; private set; }
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