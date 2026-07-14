using System;
using Flow.Grains.Events;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Orleans;

namespace Flow.Grains.Plan.Sentry.Events
{
    // D5 - see SentryStore's class remarks for what an OccurrenceToken is and why this event now
    // carries one instead of just the OnPart that occurred.
    [GenerateSerializer]
    public class OnPartOccurred : IActorStampedEvent
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        [Id(1)]
        public OnPart OnPart { get; set; }

        // Null for a CaseFileItemOnPart occurrence (no distinguishing instance-id concept exists -
        // see SentryStore's remarks); PlanItemTransitionedEvent.SourceInstanceId for a
        // PlanItemOnPart occurrence.
        [Id(2)]
        public string OccurrenceToken { get; set; }

        // ADO #59 - see IActorStampedEvent's remarks: additive fields on this already-shipped
        // type, not a hierarchy change.
        [Id(3)]
        public Guid ActorPrincipalId { get; set; }
        [Id(4)]
        public ActorPrincipalType ActorPrincipalType { get; set; }
        [Id(5)]
        public string ActorOnBehalfOf { get; set; }
    }
}