using System;
using Wayfinder.Grains.Plan;
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

        // Design 05 section A.5 - the case model as resolved once at CaseGrain.Create, threaded
        // to this element rather than fetched. Null for elements defined outside a case Create
        // flow. Carried on the shared base event so every CmmnElement kind (plan item, sentry,
        // planning table, case) journals it the same way; see CaseModelPin.
        [Id(2)]
        public CaseModelPin Pin { get; set; }
    }
}
