using System;

namespace Flow.Grains.Plan.PlanItem
{
    public class CriterionStore
    {
        public string SatisfiedByAddress { get; private set; }
        public CriterionState State { get; private set; }

        public void Apply(EntryCriterionSatisfied @event)
        {
            SatisfiedByAddress = $"{@event.SourceScope}.{@event.SourceId}";
            State = CriterionState.Satisfied;
            if (@event.OnPartOccurred)
            {
                State |= CriterionState.OnPartOccurred;
            }
        }

        public void Apply(ExitCriterionSatisfied @event)
        {
            SatisfiedByAddress = $"{@event.SourceScope}.{@event.SourceId}";
            State = CriterionState.Satisfied;
            if (@event.OnPartOccurred)
            {
                State |= CriterionState.OnPartOccurred;
            }
        }
    }

    [Flags]
    public enum CriterionState
    {
        Unsatisfied = 0,

        Satisfied = 1,
        OnPartOccurred = 2
    }
}
