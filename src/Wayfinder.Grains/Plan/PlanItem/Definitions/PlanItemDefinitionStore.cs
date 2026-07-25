using System;
using Flow.Grains.Interfaces.Model;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Definitions
{
    [GenerateSerializer]
    public class PlanItemDefinitionStore
    {
        [Id(0)]
        public PlanItemDefinition PlanItemDefinition { get; set; }
        [Id(1)]
        public DateTime? Created { get; set; }
    }
}
