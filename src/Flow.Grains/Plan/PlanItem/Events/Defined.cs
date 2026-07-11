using System;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class Defined : CmmnElementDefined<Interfaces.Model.PlanItem>
    {
        [Id(0)]
        public PlanItemDefinition PlanItemDefinition { get; set; }
        [Id(1)]
        public int Repetition { get; set; }
        // Parent's definition id (Host.DefinitionId at the moment StageBehavior.CreateChild
        // defines this child) - persisted so Activate's Resume re-arms the parent-transition
        // subscription with the right key after reactivation (#63). Null for items defined
        // without a known parent (e.g. PlanItemGrain.Define's bare 2-arg overload).
        [Id(2)]
        public string ParentDefinitionId { get; set; }
    }
}