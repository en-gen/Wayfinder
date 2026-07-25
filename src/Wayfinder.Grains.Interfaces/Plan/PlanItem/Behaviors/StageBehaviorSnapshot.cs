using System;
using System.Collections.Generic;
using Orleans;

namespace Wayfinder.Grains.Interfaces.Plan.PlanItem.Behaviors
{
    [GenerateSerializer]
    public class StageBehaviorSnapshot
    {
        // PlanItemId => PlanItemInstanceId => Repetition
        [Id(0)]
        public IDictionary<string, IDictionary<string, int>> Children { get; set; }
    }
}
