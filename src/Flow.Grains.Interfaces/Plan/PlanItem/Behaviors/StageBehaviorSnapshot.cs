using System;
using System.Collections.Generic;

namespace Flow.Grains.Interfaces.Plan.PlanItem.Behaviors
{
    [Serializable]
    public class StageBehaviorSnapshot
    {
        // PlanItemDefinitionId => PlanItemInstanceId => Repetition
        public IDictionary<string, IDictionary<string, int>> Children { get; set; }
    }
}
