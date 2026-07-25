using System;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
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
        // Parent's full DEFINITION-scope path (Host.DefinitionScope at the moment StageBehavior.
        // CreateChild defines this child) - the definition-tree counterpart to ParentDefinitionId
        // above. Used as the search scope for this item's OWN CaseDefinitionGrain.
        // GetPlanItemDefinition lookup, and combined with this item's own Definition.Id to become
        // ITS children's ParentDefinitionScope in turn. Null for items defined without a known
        // parent (e.g. PlanItemGrain.Define's bare 2-arg overload) - PlanItemGrain.DefineRepetition
        // falls back to the (pre-#65) instance-scope-derived lookup in that case (#65).
        [Id(3)]
        public string ParentDefinitionScope { get; set; }
    }
}
