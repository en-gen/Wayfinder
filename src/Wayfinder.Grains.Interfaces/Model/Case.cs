using System.Collections.Generic;

namespace Flow.Grains.Interfaces.Model
{
    public partial class Case : IBehaviorDefinition
    {
        public ICollection<EntryCriterion> EntryCriteria { get; } = new List<EntryCriterion>();
        public ICollection<ExitCriterion> ExitCriteria { get; } = new List<ExitCriterion>();
        public PlanItemControl ItemControl => null;
    }
}
