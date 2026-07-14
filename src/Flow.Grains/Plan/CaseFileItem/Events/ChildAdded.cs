using System;
using Flow.Grains.Events;
using Flow.Grains.Interfaces;
using Orleans;

namespace Flow.Grains.Plan.CaseFileItem.Events
{
    // 8.3 - CaseFileItem Lifecycle, Table 8.2
    // ~~~~~
    // add child: Available -> Available. Another CaseFileItem instance is added to the children
    // relationship (5.3.2's CaseFileItem.children).
    [GenerateSerializer]
    public class ChildAdded : IActorStampedEvent
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        [Id(1)]
        public string ChildCaseFileItemId { get; set; }

        // ADO #59 - see IActorStampedEvent's remarks for why this (and Sentry's own event types)
        // implement the interface directly instead of adopting BaseUpdate: additive new [Id(n)]
        // fields on this already-shipped type, not a hierarchy change.
        [Id(2)]
        public Guid ActorPrincipalId { get; set; }
        [Id(3)]
        public ActorPrincipalType ActorPrincipalType { get; set; }
        [Id(4)]
        public string ActorOnBehalfOf { get; set; }
    }
}
