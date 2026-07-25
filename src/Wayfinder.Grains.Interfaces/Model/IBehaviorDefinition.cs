using System.Collections.Generic;

namespace Wayfinder.Grains.Interfaces.Model
{
    public interface IBehaviorDefinition
    {
        ICollection<EntryCriterion> EntryCriteria { get; }
        ICollection<ExitCriterion> ExitCriteria { get; }
        PlanItemControl ItemControl { get; }
    }
}
