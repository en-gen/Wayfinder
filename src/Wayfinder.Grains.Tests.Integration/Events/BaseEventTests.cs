using System;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Events
{
    // Verifies the concern from work item #13, fix #2: BaseEvent.Occurred is set once at
    // construction (DateTime.UtcNow property initializer, get-only, [Id(0)]) and must survive
    // whatever transport/serialization it passes through - it must not be silently regenerated
    // to "now" by the receiving side.
    //
    // BaseEvent-derived events (PlanItemTransitionedEvent, CaseFileItemTransitionedEvent,
    // SentrySatisfiedEvent, PlanItemRepetitionCriteriaMetEvent) are never RaiseEvent'd on a
    // JournaledGrain and therefore never replayed from the event-sourced log - they are only ever
    // constructed once and published on an Orleans stream (Host.Publish -> GetCaseEventStream(...)
    // .OnNextAsync(@event)). Both the production silo (Wayfinder.Silo/Program.cs) and this test cluster
    // (ClusterFixture) configure AddMemoryStreams("Default"), which - unlike a raw in-process
    // delegate call - does serialize/deserialize the event through Orleans's codec even though the
    // transport is in-memory. That is the one and only round trip Occurred actually needs to
    // survive in this codebase, so this test exercises exactly that path with a real TestCluster
    // stream rather than asserting anything about journaled replay (which does not apply here).
    [Collection(ClusterCollection.Name)]
    public class BaseEventTests
    {
        private readonly IClusterClient _clusterClient;

        public BaseEventTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;
        }

        [Theory, AutoData]
        public async Task Publish__Given_PlanItemTransitionedEvent__When_ReceivedFromStream__Then_OccurredIsPreserved(
            Guid caseInstanceId, string scope, string instanceId, string definitionId)
        {
            var @event = new PlanItemTransitionedEvent(
                scope,
                instanceId,
                definitionId,
                PlanItemTransition.Occur,
                PlanItemState.Available,
                PlanItemState.Completed);

            var expectedOccurred = @event.Occurred;

            // prove the assertion below would actually catch regeneration-to-"now": if Occurred
            // were re-stamped on the receiving side, it would land measurably later than this.
            await Task.Delay(TimeSpan.FromMilliseconds(50));

            var tcs = new TaskCompletionSource<PlanItemTransitionedEvent>();

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<PlanItemTransitionedEvent>(caseInstanceId, definitionId)
                .SubscribeAsync((received, token) =>
                {
                    tcs.SetResult(received);
                    return Task.CompletedTask;
                });

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<PlanItemTransitionedEvent>(caseInstanceId, definitionId)
                .OnNextAsync(@event);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().Be(tcs.Task, "the subscribed handler should receive the published event");

            var receivedEvent = await tcs.Task;

            receivedEvent.Should().NotBeSameAs(@event, "the event must have actually crossed the stream's serialization boundary");
            receivedEvent.Occurred.Should().Be(expectedOccurred, "Occurred must survive stream transport unchanged, not be regenerated to the receive time");
        }
    }
}
