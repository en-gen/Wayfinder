using Orleans;

namespace Wayfinder.Grains.Interfaces.Plan.PlanItem
{
    [GenerateSerializer]
    public class CriterionSnapshot
    {
        [Id(0)]
        public string SatisfiedByAddress { get; set; }
        [Id(1)]
        public CriterionState State { get; set; }
    }
}
