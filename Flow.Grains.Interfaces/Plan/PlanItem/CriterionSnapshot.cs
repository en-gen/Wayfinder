using System;

namespace Flow.Grains.Interfaces.Plan.PlanItem
{
    [Serializable]
    public class CriterionSnapshot
    {
        public string SatisfiedByAddress { get; set; }
        public CriterionState State { get; set; }
    }
}
