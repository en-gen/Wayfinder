using System;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Orleans;

namespace Wayfinder.Grains.Plan.CaseFileItem.Events
{
    // 8.3 - CaseFileItem Lifecycle, Table 8.2
    // ~~~~~
    // delete: Available -> Discarded. Terminal state.
    [GenerateSerializer]
    public class Discarded : IActorStampedEvent
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        // ADO #59 - see ChildAdded's remarks (same additive-field treatment).
        [Id(1)]
        public Guid ActorPrincipalId { get; set; }
        [Id(2)]
        public ActorPrincipalType ActorPrincipalType { get; set; }
        [Id(3)]
        public string ActorOnBehalfOf { get; set; }
    }
}
