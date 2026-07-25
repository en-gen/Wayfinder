using System;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Orleans;

namespace Wayfinder.Grains.Plan.Sentry.Events
{
    // D10 - see SentryGrain.HandleOnPartOccurred's remarks for when this is raised (an IfPart that
    // could not be evaluated at all, distinct from one that evaluated cleanly to FALSE).
    [GenerateSerializer]
    public class Faulted : IActorStampedEvent
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        // ADO #59 - see IActorStampedEvent's remarks: additive fields on this already-shipped
        // type, not a hierarchy change.
        [Id(1)]
        public Guid ActorPrincipalId { get; set; }
        [Id(2)]
        public ActorPrincipalType ActorPrincipalType { get; set; }
        [Id(3)]
        public string ActorOnBehalfOf { get; set; }
    }
}
