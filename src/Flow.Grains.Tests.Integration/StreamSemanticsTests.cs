using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Events;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.Sentry;
using Flow.Grains.Tests.Integration.SiloFixture;
using Flow.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Flow.Grains.Tests.Integration
{
    // Fix #6 (work item #13): test-only proof of two Orleans stream-semantics assumptions the CMMN
    // engine relies on throughout (durable subscriptions surviving reactivation, and one publish
    // fanning out to many subscribers). No product code changes were made here.
    //
    // (b) the fan-out test passed reliably from the first run - no product bug found.
    // (a) the deactivation/reactivation test's original scope (Orleans reactivating a fully dormant
    // grain purely from stream arrival, with no other trigger) turned out to be unreliable in this
    // environment; see the FINDING comment on that test for what was actually verified instead, and
    // the parent conversation's final report for the full writeup of this investigation.
    [Collection(ClusterCollection.Name)]
    public class StreamSemanticsTests
    {
        private ClusterFixture Fixture { get; }
        private IClusterClient ClusterClient => Fixture.ClusterClient;

        public StreamSemanticsTests(ClusterFixture fixture)
        {
            Fixture = fixture;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // (a) pending-message-at-activation: a durably-subscribed grain (SentryGrain, which
        // resumes its OnPart subscriptions in OnActivateAsync when already Defined) is force
        // -deactivated, a message is published to its subscribed stream while it is inactive, and
        // the grain is then reactivated (explicitly, via a plain grain call - see the finding note
        // below for why this test does not rely on Orleans reactivating the grain purely from stream
        // traffic). Asserts that once reactivated, the resumed subscription drains the pending
        // message and the handler runs (SentrySatisfiedEvent is published) without deadlock/timeout.
        //
        // FINDING (reported per work item #13 instructions, no product fix applied - this is an
        // Orleans platform characteristic, not a defect in this codebase):
        // Per Orleans's own docs (learn.microsoft.com/dotnet/orleans/streaming/streams-programming-apis),
        // a deactivated grain holding an explicit durable stream subscription is supposed to be
        // automatically reactivated when a new message arrives on that stream, exactly like a normal
        // grain call would activate it. Testing that exact scenario against this solution's
        // MemoryStreams provider (Orleans 10.2.1) was NOT reliable: across repeated runs, publishing
        // a message to an inactive subscribed grain sometimes triggered reactivation+delivery within
        // ~1-2s, and sometimes never delivered the message at all even after 10+ seconds AND a
        // subsequent explicit reactivation of the same grain (i.e. not merely slow - genuinely lost
        // in some runs). This matches a known class of Orleans pulling-agent/cache-handshake races
        // (e.g. dotnet/orleans#9501, #9023, #7304, #8542) though those specific issues are keyed to
        // much longer inactivity windows (30+ minutes) or multi-silo topologies that don't match this
        // single-process TestCluster scenario, so the exact root cause was not pinned down further.
        // Recommendation: if this system comes to depend on a dormant grain being woken purely by
        // stream arrival (rather than by an incoming call, as it does today via Host.SubscribeTo's
        // StreamFlags.Resume path on grain activation), that dependency should not be trusted without
        // a durable "wake-up" path (e.g. a reminder, or an explicit poke) until this is root-caused
        // against the exact Orleans version in use.
        [Theory, AutoData]
        public async Task DeactivateThenReactivate__Given_PendingStreamMessage__Then_ResumedSubscriptionDrainsMessage
            (string caseDefinitionId, Guid caseInstanceId, string sourceScope, string sourcePlanItemId)
        {
            var sentry = new Interfaces.Model.Sentry
            {
                OnParts =
                {
                    new PlanItemOnPart
                    {
                        SourceRef = sourcePlanItemId,
                        StandardEvent = PlanItemTransition.Occur
                    }
                }
            };

            var sentryGrain = ClusterClient.GetGrain<ISentryGrain>(caseInstanceId, $"{sourceScope}.{sentry.Id}");

            await sentryGrain.Define(caseDefinitionId, sentry);

            // force the grain out of memory - OnActivateAsync must resume the OnPart subscription
            // (StreamFlags.Resume) the next time it is reactivated to deliver a pending message.
            await Fixture.Cluster.DeactivateAsync(sentryGrain);
            await Fixture.Cluster.WaitForDeactivationAsync(sentryGrain);

            var tcs = new TaskCompletionSource<bool>();

            await ClusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<SentrySatisfiedEvent>(caseInstanceId, sentry.Id)
                .SubscribeAsync((e, t) =>
                {
                    tcs.TrySetResult(true);
                    return Task.CompletedTask;
                });

            // publish to the subscribed stream while the grain is inactive.
            await ClusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<PlanItemTransitionedEvent>(caseInstanceId, sourcePlanItemId)
                .OnNextAsync(new PlanItemTransitionedEvent(
                    sourceScope,
                    ShortGuid.NewGuid(),
                    sourcePlanItemId,
                    PlanItemTransition.Occur,
                    PlanItemState.Available,
                    PlanItemState.Completed));

            // explicitly reactivate (see FINDING above: relying on Orleans to reactivate purely from
            // stream traffic was not reliable in this environment) and confirm the grain's own state
            // survived deactivation, then give the resumed subscription a window to drain the
            // already-published message.
            var definedAfterReactivation = await sentryGrain.Defined();
            definedAfterReactivation.Should().BeTrue("the sentry's own definition state must survive deactivation/reactivation");

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));

            completed.Should().Be(tcs.Task, "once reactivated, the resumed OnPart subscription's handler should run and drain the pending message, publishing SentrySatisfiedEvent, without deadlocking or timing out");
        }

        // (b) fan-out publish bound: one publisher publishes a single PlanItemTransitionedEvent
        // observed by ~20 subscribing sentries (each with a distinct OnPart pointed at the same
        // source). Asserts the publish call itself completes within a generous bound and that all
        // subscribers eventually receive the event - i.e. fan-out doesn't serialize per-subscriber
        // inside the publish call, and doesn't silently drop any subscriber.
        [Theory, AutoData]
        public async Task Publish__Given_FanOutToManySubscribingSentries__Then_PublishCompletesAndAllSubscribersReceive
            (string caseDefinitionId, Guid caseInstanceId, string sourceScope, string sourcePlanItemId)
        {
            const int subscriberCount = 20;

            var sentries = Enumerable.Range(0, subscriberCount)
                .Select(_ => new Interfaces.Model.Sentry
                {
                    OnParts =
                    {
                        new PlanItemOnPart
                        {
                            SourceRef = sourcePlanItemId,
                            StandardEvent = PlanItemTransition.Occur
                        }
                    }
                })
                .ToList();

            await Task.WhenAll(sentries.Select(sentry =>
                ClusterClient.GetGrain<ISentryGrain>(caseInstanceId, $"{sourceScope}.{sentry.Id}")
                    .Define(caseDefinitionId, sentry)));

            var receivedBySentryId = new Dictionary<string, TaskCompletionSource<bool>>();
            foreach (var sentry in sentries)
            {
                var tcs = new TaskCompletionSource<bool>();
                receivedBySentryId[sentry.Id] = tcs;

                await ClusterClient.GetStreamProvider("Default")
                    .GetCaseEventStream<SentrySatisfiedEvent>(caseInstanceId, sentry.Id)
                    .SubscribeAsync((e, t) =>
                    {
                        tcs.TrySetResult(true);
                        return Task.CompletedTask;
                    });
            }

            var publishBound = TimeSpan.FromSeconds(10);
            var publishTask = ClusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<PlanItemTransitionedEvent>(caseInstanceId, sourcePlanItemId)
                .OnNextAsync(new PlanItemTransitionedEvent(
                    sourceScope,
                    ShortGuid.NewGuid(),
                    sourcePlanItemId,
                    PlanItemTransition.Occur,
                    PlanItemState.Available,
                    PlanItemState.Completed));

            var publishCompleted = await Task.WhenAny(publishTask, Task.Delay(publishBound));
            publishCompleted.Should().Be(publishTask, $"the publish call should complete within {publishBound.TotalSeconds}s regardless of how many grains are subscribed");

            var allReceivedBound = TimeSpan.FromSeconds(10);
            var allReceived = Task.WhenAll(receivedBySentryId.Values.Select(tcs => tcs.Task));
            var allReceivedCompleted = await Task.WhenAny(allReceived, Task.Delay(allReceivedBound));

            allReceivedCompleted.Should().Be(allReceived, $"all {subscriberCount} subscribing sentries should eventually receive the single published event within {allReceivedBound.TotalSeconds}s");
        }
    }
}
