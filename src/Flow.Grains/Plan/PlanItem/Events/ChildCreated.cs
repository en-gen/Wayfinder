using System;
using Flow.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class ChildCreated : BaseUpdate
    {
        // The child PlanItem's OWN Id (the <planItem> element's id), NOT its
        // DefinitionRef/PlanItemDefinition.Id - see StageBehavior.CreateChild, which populates
        // this from child.Id. Property name kept aligned with that semantics (renamed from the
        // misleading PlanItemDefinitionId); [Id(0)] is unchanged for wire/replay compatibility.
        [Id(0)]
        public string PlanItemId { get; set; }
        [Id(1)]
        public string PlanItemInstanceId { get; set; }
        [Id(2)]
        public int Repetition { get; set; }
    }
}
