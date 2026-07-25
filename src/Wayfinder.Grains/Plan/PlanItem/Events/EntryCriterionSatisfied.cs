using System;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class EntryCriterionSatisfied : CriterionSatisfied
    {
    }
}
