using System;
using System.Text.Json.Nodes;
using Flow.Grains.Interfaces;
using Flow.Grains.Plan.CaseFileItem.Events;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;
using Xunit;

namespace Flow.Grains.Tests.Integration.Events
{
    // ADO #58 - the version-history equivalent of ActorStampingReplaySafetyTests: proves that
    // adding ValueChanged.Transition (a new, additive [Id(5)] field - see ValueChanged's remarks)
    // does not fault deserialization of a ValueChanged event persisted before this field existed.
    // There is no REAL pre-#58 persisted data to replay against, so this reconstructs the exact
    // pre-#58 wire shape (post-#59: Updated/Value/ActorPrincipalId/ActorPrincipalType/
    // ActorOnBehalfOf at Id(0..4), no Transition) via a same-shaped stand-in type, exactly the
    // pattern ActorStampingReplaySafetyTests established.
    //
    // Transition is nullable specifically so a legacy event's missing field defaults to null (an
    // explicit "unknown/not recorded" signal) rather than silently defaulting to whichever
    // CaseFileItemTransition member happens to be enum value 0 (AddChild - not even a
    // value-carrying transition) and mis-reporting a legacy Create/Update/Replace as something it
    // never was. GetHistory's mapping (CaseFileItemGrain.GetHistory) copies this field verbatim,
    // so a null Transition flows through to the version descriptor rather than faulting.
    [Collection(ClusterCollection.Name)]
    public class CaseFileItemVersionHistoryReplaySafetyTests
    {
        private readonly Serializer _serializer;

        public CaseFileItemVersionHistoryReplaySafetyTests(ClusterFixture fixture)
        {
            _serializer = fixture.ClusterClient.ServiceProvider.GetRequiredService<Serializer>();
        }

        // Stand-in for ValueChanged's shape exactly as it existed after ADO #59 but before ADO
        // #58: Updated/Value/the three actor fields, no Transition.
        [GenerateSerializer]
        public class LegacyValueChangedShape
        {
            [Id(0)]
            public DateTime Updated { get; set; }
            [Id(1)]
            public JsonNode Value { get; set; }
            [Id(2)]
            public Guid ActorPrincipalId { get; set; }
            [Id(3)]
            public ActorPrincipalType ActorPrincipalType { get; set; }
            [Id(4)]
            public string ActorOnBehalfOf { get; set; }
        }

        [Fact]
        public void Deserialize__Given_PreVersionHistoryValueChangedWireShape__When_ReadAsCurrentType__Then_NoFaultAndTransitionDefaultsNull()
        {
            var legacy = new LegacyValueChangedShape
            {
                Updated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Value = JsonValue.Create("legacy value"),
                ActorPrincipalId = Guid.NewGuid(),
                ActorPrincipalType = ActorPrincipalType.Client,
                ActorOnBehalfOf = "legacy-on-behalf-of"
            };

            var bytes = _serializer.SerializeToArray(legacy);

            var current = _serializer.Deserialize<ValueChanged>(bytes);

            current.Updated.Should().Be(legacy.Updated);
            current.Value.ToJsonString().Should().Be(legacy.Value.ToJsonString());
            current.ActorPrincipalId.Should().Be(legacy.ActorPrincipalId);
            current.ActorPrincipalType.Should().Be(legacy.ActorPrincipalType);
            current.ActorOnBehalfOf.Should().Be(legacy.ActorOnBehalfOf);
            current.Transition.Should().BeNull(
                "a pre-#58 ValueChanged event never carried a Transition and must default to null " +
                "(an explicit unknown signal), never a guessed CaseFileItemTransition value");
        }
    }
}
