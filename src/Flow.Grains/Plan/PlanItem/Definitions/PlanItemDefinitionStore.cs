using System;
using Flow.Grains.Interfaces.Model;

namespace Flow.Grains.Plan.PlanItem.Definitions
{
    [Serializable]
    public class PlanItemDefinitionStore
    {
        public PlanItemDefinition PlanItemDefinition { get; set; }
        public DateTime? Created { get; set; }
    }
}
