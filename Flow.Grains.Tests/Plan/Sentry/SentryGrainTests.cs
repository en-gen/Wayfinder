using System;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Events;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.Sentry;
using Flow.Grains.Tests.Helpers;
using Flow.Grains.Tests.SiloFixture;
using FluentAssertions;
using Orleans;
using Orleans.Hosting;
using Orleans.Streams;
using Xunit;

namespace Flow.Grains.Tests.Plan.Sentry
{
    [Collection(ClusterCollection.Name)]
    public class SentryGrainTests
    {
        private ISiloHost SiloHost { get; }
        private IClusterClient ClusterClient { get; }

        public SentryGrainTests(ClusterFixture fixture)
        {
            SiloHost = fixture.SiloHost;
            ClusterClient = fixture.ClusterClient;
        }

        [Theory, AutoData]
        public async Task HandlePlanItemTransitioned__Given_PlanItemTransitionedEvent__When_SingleOnPart__Then_PublishSentrySatisfiedEvent
            (Guid caseDefinitionId, Guid caseInstanceId, string sourceScope, string sourcePlanItemId)
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

            var subject = ClusterClient.GetGrain<ISentryGrain>(caseInstanceId, $"{sourceScope}.{sentry.Id}");

            await subject.Define(caseDefinitionId, sentry);

            var tcs = new TaskCompletionSource<bool>();

            await ClusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<SentrySatisfiedEvent>(caseInstanceId, sentry.Id)
                .SubscribeAsync((e, t) =>
                {
                    e.SourceScope.Should().Be(sourceScope);
                    e.SourceDefinitionId.Should().Be(sentry.Id);
                    e.OnPartOccurred.Should().BeTrue();

                    tcs.SetResult(true);

                    return Task.CompletedTask;
                });

            await ClusterClient.GetStreamProvider("Default")
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
            (Guid caseDefinitionId, Guid caseInstanceId, string sourceScope, string sourcePlanItemId)
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

            var subject = ClusterClient.GetGrain<ISentryGrain>(caseInstanceId, $"{sourceScope}.{sentry.Id}");

            await subject.Define(caseDefinitionId, sentry);

            var tcs = new TaskCompletionSource<bool>();

            await ClusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<SentrySatisfiedEvent>(caseInstanceId, sentry.Id)
                .SubscribeAsync((e, t) =>
                {
                    e.SourceScope.Should().Be(sourceScope);
                    e.SourceDefinitionId.Should().Be(sentry.Id);
                    e.OnPartOccurred.Should().BeTrue();

                    tcs.SetResult(true);

                    return Task.CompletedTask;
                });

            await ClusterClient.GetStreamProvider("Default")
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
        public async Task HandlePlanItemTransitioned__Given_PlanItemTransitionedEvent__When_MultiOnPart__Then_NotSatisfied
            (Guid caseDefinitionId, Guid caseInstanceId, string sourceScope, string sourcePlanItemId)
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

            var subject = ClusterClient.GetGrain<ISentryGrain>(caseInstanceId, $"{sourceScope}.{ShortGuid.NewGuid()}");

            await subject.Define(caseDefinitionId, sentry);

            var tcs = new TaskCompletionSource<bool>();

            await ClusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<SentrySatisfiedEvent>(caseInstanceId, sentry.Id)
                .SubscribeAsync((e, t) =>
                {
                    e.SourceScope.Should().Be(sourceScope);
                    e.SourceDefinitionId.Should().Be(sentry.Id);
                    e.OnPartOccurred.Should().BeTrue();

                    tcs.SetResult(true);

                    return Task.CompletedTask;
                });

            await ClusterClient.GetStreamProvider("Default")
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
    }
}
