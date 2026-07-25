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

        // ADO #58 - which of the three value-carrying CaseFileItemTransitions (Create/Update/
        // Replace - Table 8.2) raised this event. All three raise this exact same ValueChanged
        // type with no other distinguishing field (see CaseFileItemGrain.Create/ChangeValue), so
        // without this field a replayed journal could never tell an Update from a Replace, only
        // that "the value changed". Additive [Id(5)] at the next free slot, same treatment as
        // ADO #59's own actor fields above - and nullable for the same reason: a ValueChanged
        // event raised before this field existed must default to null (an explicit "not
        // recorded" signal), not silently default to CaseFileItemTransition's own zero-value
        // member (AddChild - not even a value-carrying transition, which would misreport a
        // legacy Create/Update/Replace as something it never was). See
        // CaseFileItemVersionHistoryReplaySafetyTests.
        [Id(5)]
        public Interfaces.Model.CaseFileItemTransition? Transition { get; set; }
    }
}
