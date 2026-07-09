using System;
using System.Threading;
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

namespace Flow.Grains.Tests.Integration.Plan.Sentry
{
    [Collection(ClusterCollection.Name)]
    public class SentryGrainTests
    {
        private readonly IClusterClient _clusterClient;

        public SentryGrainTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        [Theory, AutoData]
        public async Task HandlePlanItemTransitioned__Given_PlanItemTransitionedEvent__When_SingleOnPart__Then_PublishSentrySatisfiedEvent
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
                },
                IfPart = new IfPart
                {
                    Condition = Rules.TruthyExpression
                }
            };

            var subject = _clusterClient.GetGrain<ISentryGrain>(caseInstanceId, $"{sourceScope}.{sentry.Id}");

            await subject.Define(caseDefinitionId, sentry);

            var tcs = new TaskCompletionSource<bool>();

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<SentrySatisfiedEvent>(caseInstanceId, sentry.Id)
                .SubscribeAsync((e, t) =>
                {
                    e.SourceScope.Should().Be(sourceScope);
                    e.SourceDefinitionId.Should().Be(sentry.Id);
                    e.OnPartOccurred.Should().BeTrue();

                    tcs.SetResult(true);

                    return Task.CompletedTask;
                });

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<PlanItemTransitionedEvent>(caseInstanceId, sourcePlanItemId)
                .OnNextAsync(new PlanItemTransitionedEvent(
                    sourceScope,
                    ShortGuid.NewGuid(),
                    sourcePlanItemId,
                    PlanItemTransition.Occur,
                    PlanItemState.Available,
                    PlanItemState.Completed));

            var handlerInvoked = tcs.Task.Wait(TimeSpan.FromMilliseconds(500));

            handlerInvoked.Should().BeTrue();
        }

        [Theory, AutoData]
        public async Task HandlePlanItemTransitioned__Given_PlanItemTransitionedEvent__When_NoIfPart__Then_PublishSentrySatisfiedEvent
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

            var subject = _clusterClient.GetGrain<ISentryGrain>(caseInstanceId, $"{sourceScope}.{sentry.Id}");

            await subject.Define(caseDefinitionId, sentry);

            var tcs = new TaskCompletionSource<bool>();

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<SentrySatisfiedEvent>(caseInstanceId, sentry.Id)
                .SubscribeAsync((e, t) =>
                {
                    e.SourceScope.Should().Be(sourceScope);
                    e.SourceDefinitionId.Should().Be(sentry.Id);
                    e.OnPartOccurred.Should().BeTrue();

                    tcs.SetResult(true);

                    return Task.CompletedTask;
                });

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<PlanItemTransitionedEvent>(caseInstanceId, sourcePlanItemId)
                .OnNextAsync(new PlanItemTransitionedEvent(
                    sourceScope,
                    ShortGuid.NewGuid(),
                    sourcePlanItemId,
                    PlanItemTransition.Occur,
                    PlanItemState.Available,
                    PlanItemState.Completed));

            var handlerInvoked = tcs.Task.Wait(TimeSpan.FromMilliseconds(500));

            handlerInvoked.Should().BeTrue();
        }

        // D1 - single OnPart + IfPart, IfPart false: the OnPart occurring is necessary but not
        // sufficient. 8.5: "a sentry whose OnParts have all occurred but whose IfPart is false does
        // NOT fire." (Re-evaluation on a SUBSEQUENT occurrence is pinned by
        // CaseFileItemSentryIntegrationTests.CaseFileItemUpdate__Given_SentryWithCaseFileItemOnPartAndIfPart__Then_SentryOnlyFiresWhenConditionTrue,
        // which has a toggleable condition source; PlanItemOnPart sentries have no equivalent
        // repeatable/stateful trigger to exercise the same re-arm behavior against.)
        [Theory, AutoData]
        public async Task HandlePlanItemTransitioned__Given_PlanItemTransitionedEvent__When_IfPartFalse__Then_NotSatisfied
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
                },
                IfPart = new IfPart
                {
                    Condition = Rules.FalsyExpression
                }
            };

            var subject = _clusterClient.GetGrain<ISentryGrain>(caseInstanceId, $"{sourceScope}.{sentry.Id}");

            await subject.Define(caseDefinitionId, sentry);

            var tcs = new TaskCompletionSource<bool>();

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<SentrySatisfiedEvent>(caseInstanceId, sentry.Id)
                .SubscribeAsync((e, t) =>
                {
                    tcs.TrySetResult(true);
                    return Task.CompletedTask;
                });

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<PlanItemTransitionedEvent>(caseInstanceId, sourcePlanItemId)
                .OnNextAsync(new PlanItemTransitionedEvent(
                    sourceScope,
                    ShortGuid.NewGuid(),
                    sourcePlanItemId,
                    PlanItemTransition.Occur,
                    PlanItemState.Available,
                    PlanItemState.Completed));

            var handlerInvoked = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMilliseconds(500))) == tcs.Task;

            handlerInvoked.Should().BeFalse("the OnPart occurred but the IfPart evaluates false, so the sentry must not publish SentrySatisfiedEvent");
        }

        [Theory, AutoData]
        public async Task HandlePlanItemTransitioned__Given_PlanItemTransitionedEvent__When_MultiOnPart__Then_NotSatisfied
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
                    },
                    new PlanItemOnPart
                    {
                        SourceRef = "will_never_satisfy",
                        StandardEvent = PlanItemTransition.Occur
                    }
                }
            };

            var subject = _clusterClient.GetGrain<ISentryGrain>(caseInstanceId, $"{sourceScope}.{ShortGuid.NewGuid()}");

            await subject.Define(caseDefinitionId, sentry);

            var tcs = new TaskCompletionSource<bool>();

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<SentrySatisfiedEvent>(caseInstanceId, sentry.Id)
                .SubscribeAsync((e, t) =>
                {
                    e.SourceScope.Should().Be(sourceScope);
                    e.SourceDefinitionId.Should().Be(sentry.Id);
                    e.OnPartOccurred.Should().BeTrue();

                    tcs.SetResult(true);

                    return Task.CompletedTask;
                });

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<PlanItemTransitionedEvent>(caseInstanceId, sourcePlanItemId)
                .OnNextAsync(new PlanItemTransitionedEvent(
                    sourceScope,
                    ShortGuid.NewGuid(),
                    sourcePlanItemId,
                    PlanItemTransition.Occur,
                    PlanItemState.Available,
                    PlanItemState.Completed));

            var handlerInvoked = tcs.Task.Wait(TimeSpan.FromMilliseconds(500));

            handlerInvoked.Should().BeFalse();
        }

        // Pinning test for the double-satisfy guard in SentryGrain.HandleOnPartOccurred: at-least
        // -once stream delivery can redeliver the same PlanItemTransitionedEvent. Without a guard,
        // the redelivery would re-raise OnPartOccurred, re-run the satisfaction check and IfPart
        // evaluation, and publish a second SentrySatisfiedEvent even though the sentry was already
        // satisfied by the first delivery.
        [Theory, AutoData]
        public async Task HandlePlanItemTransitioned__Given_SameEventDeliveredTwice__Then_PublishSentrySatisfiedEventExactlyOnce
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
                },
                IfPart = new IfPart
                {
                    Condition = Rules.TruthyExpression
                }
            };

            var subject = _clusterClient.GetGrain<ISentryGrain>(caseInstanceId, $"{sourceScope}.{sentry.Id}");

            await subject.Define(caseDefinitionId, sentry);

            var receivedCount = 0;
            var firstReceived = new TaskCompletionSource<bool>();

            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<SentrySatisfiedEvent>(caseInstanceId, sentry.Id)
                .SubscribeAsync((e, t) =>
                {
                    Interlocked.Increment(ref receivedCount);
                    firstReceived.TrySetResult(true);

                    return Task.CompletedTask;
                });

            var transitionEvent = new PlanItemTransitionedEvent(
                sourceScope,
                ShortGuid.NewGuid(),
                sourcePlanItemId,
                PlanItemTransition.Occur,
                PlanItemState.Available,
                PlanItemState.Completed);

            var transitionedStream = _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<PlanItemTransitionedEvent>(caseInstanceId, sourcePlanItemId);

            // simulate at-least-once redelivery of the exact same logical transition
            await transitionedStream.OnNextAsync(transitionEvent);
            await transitionedStream.OnNextAsync(transitionEvent);

            var firstArrived = firstReceived.Task.Wait(TimeSpan.FromMilliseconds(500));
            firstArrived.Should().BeTrue("the sentry should still be satisfied by the first delivery");

            // give any (incorrect) second publish a chance to arrive before asserting the count
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            receivedCount.Should().Be(1, "redelivery of the same transition must not cause a second SentrySatisfiedEvent publish");
        }
    }
}
