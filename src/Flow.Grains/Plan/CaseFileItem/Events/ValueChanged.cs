using System;
using System.Text.Json.Nodes;
using Flow.Grains.Events;
using Flow.Grains.Interfaces;
using Orleans;

namespace Flow.Grains.Plan.CaseFileItem.Events
{
    // Journaled for the update/replace/addChild/removeChild/addReference/removeReference
    // self-transitions (Table 8.2) - all of them mutate CaseFileItem content and are otherwise
    // identical from the store's point of view (the distinct standardEvent each one publishes
    // is what SentryGrain/TimerEventListenerBehavior key on; the store only needs the new Value).
    [GenerateSerializer]
    public class ValueChanged : IActorStampedEvent
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        [Id(1)]
        public JsonNode Value { get; set; }

        // ADO #59 - see ChildAdded's remarks (same additive-field treatment).
        [Id(2)]
        public Guid ActorPrincipalId { get; set; }
        [Id(3)]
        public ActorPrincipalType ActorPrincipalType { get; set; }
        [Id(4)]
        public string ActorOnBehalfOf { get; set; }
    }
}
