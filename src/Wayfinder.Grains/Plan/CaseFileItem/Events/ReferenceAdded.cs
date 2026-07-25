using System;
using Flow.Grains.Events;
using Flow.Grains.Interfaces;
using Orleans;

namespace Flow.Grains.Plan.CaseFileItem.Events
{
    // 8.3 - CaseFileItem Lifecycle, Table 8.2
    // ~~~~~
    // add reference: Available -> Available. Another CaseFileItem instance is added to the target
    // reference relationship (5.3.2's CaseFileItem.targetRefs).
    [GenerateSerializer]
    public class ReferenceAdded : IActorStampedEvent
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        [Id(1)]
        public string TargetCaseFileItemId { get; set; }

        // ADO #59 - see ChildAdded's remarks (same additive-field treatment).
        [Id(2)]
        public Guid ActorPrincipalId { get; set; }
        [Id(3)]
        public ActorPrincipalType ActorPrincipalType { get; set; }
        [Id(4)]
        public string ActorOnBehalfOf { get; set; }
    }
}
