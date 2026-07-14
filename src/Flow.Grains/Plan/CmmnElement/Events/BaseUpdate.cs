using System;
using Flow.Grains.Events;
using Flow.Grains.Interfaces;
using Orleans;

namespace Flow.Grains.Plan.CmmnElement.Events
{
    [GenerateSerializer]
    public abstract class BaseUpdate : IActorStampedEvent
    {
        [Id(0)]
        public DateTime Updated { get; set; } = DateTime.UtcNow;

        // ADO #59 - additive: new [Id(n)] fields on an already-shipped type, not a new base class,
        // so replay of events persisted before this change defaults these harmlessly (Guid.Empty /
        // User / null) rather than faulting - see IActorStampedEvent's remarks. Stamped centrally
        // at RaiseEvent time (Events.ActorStamping), never set at each call site.
        [Id(1)]
        public Guid ActorPrincipalId { get; set; }
        [Id(2)]
        public ActorPrincipalType ActorPrincipalType { get; set; }
        [Id(3)]
        public string ActorOnBehalfOf { get; set; }
    }
}