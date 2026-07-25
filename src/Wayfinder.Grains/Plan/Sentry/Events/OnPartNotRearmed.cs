using System;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Orleans;

namespace Wayfinder.Grains.Plan.Sentry.Events
{
    // D11 - see SentryStore.Apply(OnPartNotRearmed) for why this clears only the one OnPart that
    // just completed the AND-join (superseding PR !18's IfPartNotSatisfied, which cleared every
    // recorded OnPart and was scoped to single-OnPart sentries only).
    [GenerateSerializer]
    public class OnPartNotRearmed : IActorStampedEvent
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        [Id(1)]
        public string OnPartId { get; set; }

        // ADO #59 - see Faulted's remarks (same additive-field treatment).
        [Id(2)]
        public Guid ActorPrincipalId { get; set; }
        [Id(3)]
        public ActorPrincipalType ActorPrincipalType { get; set; }
        [Id(4)]
        public string ActorOnBehalfOf { get; set; }
    }
}
