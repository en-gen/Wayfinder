using System;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [Serializable]
    public class RequiredRuleEvaluated : RuleEvaluated<bool>
    {
    }
}