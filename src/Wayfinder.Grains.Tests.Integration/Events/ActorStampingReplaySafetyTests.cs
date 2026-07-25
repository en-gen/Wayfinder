using System;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.CaseFileItem.Events;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;
using Xunit;

namespace Flow.Grains.Tests.Integration.Events
{
    // ADO #59 - proves the wire-safety claim this work item hinges on empirically, rather than by
    // assertion alone: adding new [Id(n)] fields to an already-shipped event type (or to its base
    // class) does not break deserialization of bytes written before those fields existed. There is
    // no REAL pre-#59 persisted data to replay against (the actor fields are brand new), so each
    // test here reconstructs the exact pre-#59 wire shape via a same-shaped stand-in type - proving
    // "old bytes -> new type, no fault, new fields default sensibly" for both patterns this work
    // item actually used:
    //   - CaseFileItem/Sentry events (e.g. ChildAdded): no base class before OR after - only new
    //     sibling fields appended at the SAME hierarchy level (see IActorStampedEvent's remarks for
    //     why a new base class was deliberately NOT inserted here).
    //   - BaseUpdate-derived events (e.g. Transitioned): the actor fields were added to the
    //     EXISTING BaseUpdate class itself (Id(1..3), alongside Updated's existing Id(0)) - no
    //     hierarchy change either, just new fields on an existing level.
    [Collection(ClusterCollection.Name)]
    public class ActorStampingReplaySafetyTests
    {
        private readonly Serializer _serializer;

        public ActorStampingReplaySafetyTests(ClusterFixture fixture)
        {
            _serializer = fixture.ClusterClient.ServiceProvider.GetRequiredService<Serializer>();
        }

        // Stand-in for ChildAdded's shape exactly as it existed before ADO #59 - no base class, no
        // actor fields, same [Id(n)] numbering as today's ChildAdded.Updated/ChildCaseFileItemId.
        [GenerateSerializer]
        public class LegacyChildAddedShape
        {
            [Id(0)]
            public DateTime Updated { get; set; }
            [Id(1)]
            public string ChildCaseFileItemId { get; set; }
        }

        [Fact]
        public void Deserialize__Given_PreActorStampingChildAddedWireShape__When_ReadAsCurrentType__Then_NoFaultAndActorFieldsDefault()
        {
            var legacy = new LegacyChildAddedShape
            {
                Updated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ChildCaseFileItemId = "child-1"
            };

            var bytes = _serializer.SerializeToArray(legacy);

            var current = _serializer.Deserialize<ChildAdded>(bytes);

            current.Updated.Should().Be(legacy.Updated);
            current.ChildCaseFileItemId.Should().Be(legacy.ChildCaseFileItemId);
            current.ActorPrincipalId.Should().Be(Guid.Empty);
            current.ActorPrincipalType.Should().Be(ActorPrincipalType.User);
            current.ActorOnBehalfOf.Should().BeNull();
        }

        // Stand-ins for BaseUpdate/Transitioned's shape exactly as they existed before ADO #59:
        // BaseUpdate contributed only Updated@Id(0); Transitioned's own fields (Source/Destination/
        // Trigger) are unchanged and still at Id(0)/(1)/(2) at ITS level - only BaseUpdate's level
        // gained new fields (Id(1..3)), which is exactly what this pair of shadow types reproduces.
        [GenerateSerializer]
        public abstract class LegacyBaseUpdateShape
        {
            [Id(0)]
            public DateTime Updated { get; set; }
        }

        [GenerateSerializer]
        public class LegacyTransitionedShape : LegacyBaseUpdateShape
        {
            [Id(0)]
            public PlanItemState Source { get; set; }
            [Id(1)]
            public PlanItemState Destination { get; set; }
            [Id(2)]
            public PlanItemTransition Trigger { get; set; }
        }

        [Fact]
        public void Deserialize__Given_PreActorStampingTransitionedWireShape__When_ReadAsCurrentType__Then_NoFaultAndActorFieldsDefault()
        {
            var legacy = new LegacyTransitionedShape
            {
                Updated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Source = PlanItemState.Available,
                Destination = PlanItemState.Active,
                Trigger = PlanItemTransition.Start
            };

            var bytes = _serializer.SerializeToArray(legacy);

            var current = _serializer.Deserialize<Transitioned>(bytes);

            current.Updated.Should().Be(legacy.Updated);
            current.Source.Should().Be(legacy.Source);
            current.Destination.Should().Be(legacy.Destination);
            current.Trigger.Should().Be(legacy.Trigger);
            current.ActorPrincipalId.Should().Be(Guid.Empty);
            current.ActorPrincipalType.Should().Be(ActorPrincipalType.User);
            current.ActorOnBehalfOf.Should().BeNull();
        }
    }
}
