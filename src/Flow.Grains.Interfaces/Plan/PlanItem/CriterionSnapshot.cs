namespace Flow.Grains.Interfaces.Plan.PlanItem
{
    public class CriterionSnapshot
    {
        public string SatisfiedByAddress { get; set; }
        public CriterionState State { get; set; }
    }
}