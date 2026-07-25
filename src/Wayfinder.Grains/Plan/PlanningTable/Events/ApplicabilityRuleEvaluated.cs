using Wayfinder.Grains.Plan.PlanItem.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanningTable.Events
{
    [GenerateSerializer]
    public class ApplicabilityRuleEvaluated : RuleEvaluated<bool>
    {
    }
}
