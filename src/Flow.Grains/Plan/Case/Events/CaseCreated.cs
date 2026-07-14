using System;
using Flow.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Flow.Grains.Plan.Case.Events
{
    [GenerateSerializer]
    public class CaseCreated : CmmnElementDefined<Interfaces.Model.Case>
    {
        [Id(0)]
        public int Repetition { get; set; }

        // ADO #33 - the owning tenant, stamped from CaseRequestContext.TenantId at the moment
        // CaseGrain.Create raises this event. Recorded on the event (not just the store) so a
        // replay from the event log reconstructs the same ownership CaseGrain.Trigger/GetSnapshot
        // enforce - see CaseStore.Apply(CaseCreated) for the projection.
        [Id(1)]
        public Guid TenantId { get; set; }
    }
}
