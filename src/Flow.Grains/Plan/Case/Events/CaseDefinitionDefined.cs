using System;
using Flow.Grains.Plan.CmmnElement.Events;
using Flow.Grains.Plan.PlanItem.Definitions;
using Orleans;

namespace Flow.Grains.Plan.Case.Events
{
    [GenerateSerializer]
    public class CaseDefinitionDefined : BaseCreated
    {
        [Id(0)]
        public Interfaces.Model.Case Definition { get; set; }
        [Id(1)]
        public DefinitionGraphNode DefinitionRoot { get; set; }
    }
}
