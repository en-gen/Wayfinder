using System;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class ExitCriterionSatisfied : CriterionSatisfied
    {
    }
}
