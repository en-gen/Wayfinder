using System;

namespace Wayfinder.Grains.Interfaces.Plan.PlanItem
{
    [Flags]
    public enum CriterionState
    {
        Unsatisfied = 0,

        Satisfied = 1,
        OnPartOccurred = 2
    }
}
