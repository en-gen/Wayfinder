using System;
using Orleans;

namespace Wayfinder.Grains.Plan.CmmnElement.Events
{
    [GenerateSerializer]
    public class CmmnElementDefined<TDefinition> : BaseCreated
        where TDefinition : Interfaces.Model.CmmnElement
    {
        [Id(0)]
        public string CaseDefinitionId { get; set; }

        [Id(1)]
        public TDefinition Definition { get; set; }
    }
}
