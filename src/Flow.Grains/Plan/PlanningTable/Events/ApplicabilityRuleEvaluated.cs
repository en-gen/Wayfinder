using Flow.Grains.Plan.PlanItem.Events;
using Orleans;

namespace Flow.Grains.Plan.PlanningTable.Events
{
    [GenerateSerializer]
    public class ApplicabilityRuleEvaluated : RuleEvaluated<bool>
    {
    }
}
