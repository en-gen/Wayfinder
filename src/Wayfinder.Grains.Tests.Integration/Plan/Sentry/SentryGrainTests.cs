using System;
using System.Text.Json.Nodes;
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

            // Async wait, generous window: the synchronous 500ms Wait() this replaces starved the
            // thread pool under CI contention (builds 66/67 - passed locally, timed out in CI once
            // the conformance suite joined the same collection). Matches this file's newer idiom.
            var handlerInvoked = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10))) == tcs.Task;

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

            var handlerInvoked = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10))) == tcs.Task;

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

            // Must-NOT-arrive assertion: async, bounded window (a longer wait only makes a false
            // pass less likely, never more; 2s balances confidence against suite runtime).
            var handlerInvoked = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2))) == tcs.Task;

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

            var firstArrived = await Task.WhenAny(firstReceived.Task, Task.Delay(TimeSpan.FromSeconds(10))) == firstReceived.Task;
            firstArrived.Should().BeTrue("the sentry should still be satisfied by the first delivery");

            // give any (incorrect) second publish a chance to arrive before asserting the count
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            receivedCount.Should().Be(1, "redelivery of the same transition must not cause a second SentrySatisfiedEvent publish");
        }

        // D3 - THE FLAGSHIP TEST for standalone-IfPart sentries.
        //
        // 8.5's last sentence: "Sentries with no OnPart must have an IfPart, and that IfPart will be
        // evaluated for all CaseFileItem events because IfPart expressions are based on CaseFileItem
        // properties." A 0-OnPart Sentry has no per-item sourceRef to subscribe to - it must react to
        // ANY CaseFileItem transitioning, not just one. This drives a REAL CaseFileItemGrain (not a
        // manually-published event) to prove CaseFileItemGrain.PublishTransition's new case-wide
        // publish (CaseFileItemAddress.CaseWideSentinel) actually reaches a 0-OnPart Sentry's IfPart
        // re-evaluation end to end.
        [Fact]
        public async Task StandaloneIfPart__Given_AnyCaseFileItemUpdate__Then_ReEvaluatesAndSatisfiesWhenTrue()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";
            var sourceScope = ShortGuid.NewGuid();
            const string caseFileItemId = "SomeCaseFileItem";

            var sentry = new Interfaces.Model.Sentry
            {
                // Deliberately zero OnParts - the case-wide topology this Sentry depends on (D3)
                // must not require naming caseFileItemId (or any specific item) up front.
                IfPart = new IfPart
                {
                    ContextRef = caseFileItemId,
                    Condition = new Expression
                    {
                        Language = ExpressionLanguage.Jint,
                        Body = "value.flagged === true"
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
                    e.OnPartOccurred.Should().BeFalse(
                        "8.6.4's OnPart-gated repetition re-spawn does not apply to a standalone-IfPart Sentry, which has no OnPart at all");

                    tcs.TrySetResult(true);
                    return Task.CompletedTask;
                });

            var caseFileItemGrain = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

            // Create with flagged=false - the IfPart must evaluate false and the Sentry must not fire.
            await caseFileItemGrain.Create(caseDefinitionId, new Interfaces.Model.CaseFileItem { Id = caseFileItemId }, JsonNode.Parse("""{"flagged": false}"""));

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            tcs.Task.IsCompleted.Should().BeFalse("flagged=false means the IfPart evaluates false - the standalone-IfPart Sentry must not satisfy yet");

            // Update to flagged=true - the case-wide CaseFileItem event must reach this Sentry (with
            // no OnPart/sourceRef naming this item at all) and re-evaluate the IfPart to TRUE.
            await caseFileItemGrain.Update(JsonNode.Parse("""{"flagged": true}"""));

            var satisfied = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10))) == tcs.Task;
            satisfied.Should().BeTrue(
                "the case-wide CaseFileItem Update event should have reached the standalone-IfPart Sentry (via CaseFileItemAddress.CaseWideSentinel) and satisfied it once the IfPart evaluated true");
        }

        // D3 - a standalone-IfPart Sentry must react to events from ANY CaseFileItem, not just the
        // one its own IfPart.ContextRef happens to name - proving the case-wide topology is
        // genuinely item-agnostic, not silently scoped to a single id.
        [Fact]
        public async Task StandaloneIfPart__Given_DifferentCaseFileItemTransitions__Then_StillReEvaluatesIfPart()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";
            var sourceScope = ShortGuid.NewGuid();
            const string contextCaseFileItemId = "ContextItem";
            const string otherCaseFileItemId = "OtherItem";

            var sentry = new Interfaces.Model.Sentry
            {
                IfPart = new IfPart
                {
                    ContextRef = contextCaseFileItemId,
                    Condition = new Expression
                    {
                        Language = ExpressionLanguage.Jint,
                        Body = "value.ready === true"
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
                    tcs.TrySetResult(true);
                    return Task.CompletedTask;
                });

            // ContextItem starts ready=false, so the IfPart evaluates false and the Sentry must not
            // satisfy yet.
            var contextItemGrain = _clusterClient.GetCaseFileItem(caseInstanceId, contextCaseFileItemId);
            await contextItemGrain.Create(caseDefinitionId, new Interfaces.Model.CaseFileItem { Id = contextCaseFileItemId }, JsonNode.Parse("""{"ready": false}"""));

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            tcs.Task.IsCompleted.Should().BeFalse(
                "ContextItem's own Create transition set ready=false, so the IfPart evaluates false and the Sentry must not satisfy yet");

            // OtherItem - a DIFFERENT CaseFileItem the Sentry's IfPart.ContextRef does not even name
            // - transitions. This must reach the standalone-IfPart Sentry via the case-wide stream
            // and trigger a re-evaluation, but the IfPart (still keyed to ContextItem, still
            // ready=false) must still evaluate false - proving the case-wide topology is genuinely
            // item-agnostic (any CaseFileItem's event re-triggers evaluation) without conflating that
            // with "any event automatically satisfies."
            var otherItemGrain = _clusterClient.GetCaseFileItem(caseInstanceId, otherCaseFileItemId);
            await otherItemGrain.Create(caseDefinitionId, new Interfaces.Model.CaseFileItem { Id = otherCaseFileItemId }, JsonNode.Parse("""{"unrelated": true}"""));

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            tcs.Task.IsCompleted.Should().BeFalse(
                "OtherItem transitioning must re-trigger IfPart evaluation (case-wide reach) but the IfPart still evaluates false against ContextItem's unchanged ready=false - it must not satisfy merely because SOME CaseFileItem event occurred");

            // Now ContextItem itself updates to ready=true - the Sentry must satisfy.
            await contextItemGrain.Update(JsonNode.Parse("""{"ready": true}"""));

            var satisfied = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10))) == tcs.Task;
            satisfied.Should().BeTrue(
                "ContextItem's Update transition should satisfy the standalone-IfPart Sentry once ready=true, proving both that the case-wide stream reaches it AND that satisfaction genuinely depends on the IfPart's own evaluation, not merely on case-wide event traffic");
        }

        // D3 - an IfPart evaluation fault (D10) on a standalone-IfPart Sentry must not throw or
        // crash the case-wide subscription - it should log/publish SentryFaultedEvent and continue
        // observing subsequent case-wide events, exactly as an OnPart-bearing Sentry's fault path
        // does not tear down its own subscription either.
        [Fact]
        public async Task StandaloneIfPart__Given_ContextRefNeverCreated__Then_FaultsWithoutCrashingAndNeverSatisfies()
        {
            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"case-{ShortGuid.NewGuid()}";
            var sourceScope = ShortGuid.NewGuid();
            const string missingContextItemId = "NeverCreatedItem";
            const string otherCaseFileItemId = "OtherItem";

            var sentry = new Interfaces.Model.Sentry
            {
                IfPart = new IfPart
                {
                    ContextRef = missingContextItemId,
                    Condition = new Expression
                    {
                        Language = ExpressionLanguage.Jint,
                        Body = "value.ready === true"
                    }
                }
            };

            var subject = _clusterClient.GetGrain<ISentryGrain>(caseInstanceId, $"{sourceScope}.{sentry.Id}");
            await subject.Define(caseDefinitionId, sentry);

            var satisfiedTcs = new TaskCompletionSource<bool>();
            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<SentrySatisfiedEvent>(caseInstanceId, sentry.Id)
                .SubscribeAsync((e, t) =>
                {
                    satisfiedTcs.TrySetResult(true);
                    return Task.CompletedTask;
                });

            var faultedTcs = new TaskCompletionSource<string>();
            await _clusterClient.GetStreamProvider("Default")
                .GetCaseEventStream<SentryFaultedEvent>(caseInstanceId, sentry.Id)
                .SubscribeAsync((e, t) =>
                {
                    faultedTcs.TrySetResult(e.ErrorMessage);
                    return Task.CompletedTask;
                });

            // deliberately never create "NeverCreatedItem"
            var otherItemGrain = _clusterClient.GetCaseFileItem(caseInstanceId, otherCaseFileItemId);
            await otherItemGrain.Create(caseDefinitionId, new Interfaces.Model.CaseFileItem { Id = otherCaseFileItemId }, JsonNode.Parse("""{"anything": true}"""));

            var faulted = await Task.WhenAny(faultedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10))) == faultedTcs.Task;
            faulted.Should().BeTrue("the IfPart's ContextRef names a CaseFileItem that was never created, so evaluation should fault and publish SentryFaultedEvent rather than crash the subscription");

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            satisfiedTcs.Task.IsCompleted.Should().BeFalse("a faulted IfPart evaluation must never be treated as satisfied");
        }
    }
}
