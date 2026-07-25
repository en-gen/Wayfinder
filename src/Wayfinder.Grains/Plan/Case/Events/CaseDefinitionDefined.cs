using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Wayfinder.Grains.Plan.PlanItem.Definitions;
using Orleans;

namespace Wayfinder.Grains.Plan.Case.Events
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
