using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;
using Wayfinder.Grains.Plan;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Moq;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Plan.PlanItem.Behaviors
{
    // #181 (root cause of #153) - StageBehavior.CreateChild used to subscribe to the new child's
    // event streams AFTER triggering it. A non-blocking child auto-cascades to a terminal state
    // inside its own Trigger(Create) turn and publishes its transitions - and, if it repeats, its
    // repetition-0 PlanItemRepetitionCriteriaMetEvent - from inside that turn. With the parent not
    // yet subscribed, the pulling agent resolved those messages to zero consumers and dropped them
    // permanently. Since #198 the container then holds its own completion indefinitely: a
    // permanent, silent stall of a real Case, measured at zero progress past 30s.
    //
    // WHY THESE ARE UNIT TESTS AND NOT AN INTEGRATION REPRO
    // ~~~~~
    // The organic failure rate of the real race is ~1-2% per cold first-cascade - one permanent
    // wedge in 60 isolated, unpressured runs in one campaign, 2 in 100 in another - and even under
    // deliberate CPU *scheduling* pressure (2 pinned cores plus 6 hogs on the same cores) it only
    // reached 2/25. The only lever that makes it deterministic end-to-end is injecting a delay at
    // the seam, which is precisely the sleep a committed test must not contain - and a test that
    // reproduces the defect only sometimes is not a regression guard, it is a second flake.
    //
    // So these pin the ORDERING PROPERTY that makes the race unwinnable, directly and
    // deterministically, at the seam where the defect lives, with no sleep, poll or timeout: pure
    // function-call ordering that fails identically on every run.
    public partial class StageBehaviorTests
    {
        // NOT a model of the configured provider's delivery semantics - a deliberately STRICTER
        // one, reduced to the single rule #181 turns on: a publish to a stream with zero
        // subscribers AT PUBLISH TIME is dropped and never queued for a subscriber that arrives
        // later.
        //
        // The real provider (AddMemoryStreams("Default")) is persistent and looser than that: the
        // message is enqueued, a pulling agent polls, and the window actually closes at DELIVERY,
        // not at the publish. The stricter rule is still a valid guard, and a cheaper one - any
        // ordering that survives "must be subscribed before the publish" necessarily survives
        // "must be subscribed before the agent delivers", so a fix that passes here passes there,
        // and the test needs no notion of a poll period, a queue, or elapsed time to say so.
        //
        // Everything else about streaming is deliberately not modelled; the end-to-end behaviour
        // lives in the integration suite.
        private sealed class FakeCaseEventBus
        {
            private readonly HashSet<(Type EventType, string SourceId)> _subscriptions = new();

            public List<string> Log { get; } = new();

            public void Subscribe<TEvent>(string sourceId)
            {
                _subscriptions.Add((typeof(TEvent), sourceId));
                Log.Add($"SUBSCRIBE {typeof(TEvent).Name} {sourceId}");
            }

            public bool Publish<TEvent>(string sourceId)
            {
                var delivered = _subscriptions.Contains((typeof(TEvent), sourceId));
                Log.Add($"PUBLISH {typeof(TEvent).Name} {sourceId} => {(delivered ? "DELIVERED" : "DROPPED (zero consumers)")}");
                return delivered;
            }
        }

        // Everything CreateChild touches, wired once. `callLog` records the ordered sequence of
        // outbound calls CreateChild makes, which is what tests 2 and 3 assert over.
        private sealed class CreateChildHarness
        {
            public Guid CaseInstanceId { get; } = Guid.NewGuid();
            public Interfaces.Model.PlanItem Child { get; }
            public Stage Stage { get; }
            public TestPlanItemStore Store { get; }
            public Mock<IBehaviorHost> Host { get; } = new();
            public Mock<IPlanItemInternalGrain> ChildGrain { get; } = new();
            public FakeCaseEventBus Bus { get; } = new();
            public List<string> CallLog { get; } = new();
            public StageBehavior Subject { get; }

            public CreateChildHarness()
            {
                Child = new Interfaces.Model.PlanItem { Id = ShortGuid.NewGuid() };
                Stage = new Stage { PlanItems = { Child } };
                Store = new TestPlanItemStore(piDef: Stage, initialState: PlanItemState.Active);

                StubFreshlySpawnedChildSnapshot(ChildGrain);

                var mockGrainFactory = new Mock<IGrainFactory>();
                mockGrainFactory
                    .Setup(x => x.GetGrain<IPlanItemInternalGrain>(CaseInstanceId, It.IsAny<string>(), null))
                    .Returns(ChildGrain.Object);

                Host.Setup(x => x.CaseInstanceId).Returns(CaseInstanceId);
                Host.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);
                Host.Setup(x => x.Address).Returns($"CPM.{ShortGuid.NewGuid()}");
                Host.Setup(x => x.Scope).Returns("CPM");
                Host.Setup(x => x.InstanceId).Returns(ShortGuid.NewGuid());
                Host.Setup(x => x.DefinitionId).Returns(Stage.Id);
                Host.Setup(x => x.DefinitionScope).Returns($"CPM.{Stage.Id}");
                Host.Setup(x => x.State).Returns(Store);
                Host.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                    .Callback<object>(x => Store.Apply((dynamic)x));

                Host.Setup(x => x.SubscribeTo(
                        It.IsAny<string>(),
                        It.IsAny<Func<PlanItemTransitionedEvent, StreamSequenceToken, Task>>(),
                        It.IsAny<StreamFlags>()))
                    .Callback<string, Func<PlanItemTransitionedEvent, StreamSequenceToken, Task>, StreamFlags>(
                        (sourceId, _, __) =>
                        {
                            CallLog.Add($"Subscribe({nameof(PlanItemTransitionedEvent)})");
                            Bus.Subscribe<PlanItemTransitionedEvent>(sourceId);
                        })
                    .Returns(Task.CompletedTask);

                Host.Setup(x => x.SubscribeTo(
                        It.IsAny<string>(),
                        It.IsAny<Func<PlanItemRepetitionCriteriaMetEvent, StreamSequenceToken, Task>>(),
                        It.IsAny<StreamFlags>()))
                    .Callback<string, Func<PlanItemRepetitionCriteriaMetEvent, StreamSequenceToken, Task>, StreamFlags>(
                        (sourceId, _, __) =>
                        {
                            CallLog.Add($"Subscribe({nameof(PlanItemRepetitionCriteriaMetEvent)})");
                            Bus.Subscribe<PlanItemRepetitionCriteriaMetEvent>(sourceId);
                        })
                    .Returns(Task.CompletedTask);

                ChildGrain
                    .Setup(x => x.DefineRepetition(
                        It.IsAny<string>(),
                        It.IsAny<Interfaces.Model.PlanItem>(),
                        It.IsAny<int>(),
                        It.IsAny<string>(),
                        It.IsAny<string>()))
                    .Callback(() => CallLog.Add("DefineRepetition"))
                    .Returns(Task.CompletedTask);

                Subject = new StageBehavior(Host.Object, Stage, new MockPlanItemStateMachine(Store).Object);
            }

            // The non-blocking (IsBlocking = false) child's real behaviour: Trigger(Create)
            // cascades Create -> Start -> Complete inside its own grain turn and publishes from
            // there, before control ever returns to the parent's CreateChild.
            public void ArrangeChildThatAutoCascadesInsideTriggerCreate() =>
                ChildGrain
                    .Setup(x => x.Trigger(PlanItemTransition.Create))
                    .Callback(() =>
                    {
                        CallLog.Add("Trigger(Create)");

                        // Non-short-circuiting & on purpose: BOTH publishes must actually happen
                        // (the real child publishes both, and the Bus log is the failure message),
                        // and both must have been delivered for this to be true.
                        RepetitionRequestDelivered = Bus.Publish<PlanItemTransitionedEvent>(Child.Id)
                            & Bus.Publish<PlanItemRepetitionCriteriaMetEvent>(Child.Id);
                    })
                    .ReturnsAsync((PlanItemSnapshot)null);

            public void ArrangeInertChild() =>
                ChildGrain
                    .Setup(x => x.Trigger(PlanItemTransition.Create))
                    .Callback(() => CallLog.Add("Trigger(Create)"))
                    .ReturnsAsync((PlanItemSnapshot)null);

            public void ArrangeChildThatThrowsOnTriggerCreate() =>
                ChildGrain
                    .Setup(x => x.Trigger(PlanItemTransition.Create))
                    .Callback(() => CallLog.Add("Trigger(Create)"))
                    .ThrowsAsync(new InvalidOperationException("boom"));

            public bool RepetitionRequestDelivered { get; private set; }

            public Task CreateChild(int repetition = 0) =>
                (Task)typeof(StageBehavior)
                    .GetMethod("CreateChild", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(Subject, new object[] { Child, repetition });
        }

        // THE regression guard. RED before the fix: the child's repetition-0 request is published
        // into a stream with no subscriber and dropped, which is #153's exact observed signature
        // (PUBLISHED present, RECV_CHILD_REPEATED absent, no error, no retry).
        [Fact]
        public async Task CreateChild__When_ChildAutoCascadesInsideItsOwnCreationTurn__Then_ParentIsAlreadySubscribedAndReceivesTheRepetitionRequest()
        {
            var harness = new CreateChildHarness();
            harness.ArrangeChildThatAutoCascadesInsideTriggerCreate();

            await harness.CreateChild();

            harness.RepetitionRequestDelivered.Should().BeTrue(
                "#181: a non-blocking child publishes its transitions and its repetition-0 " +
                "PlanItemRepetitionCriteriaMetEvent from INSIDE Trigger(Create); if the parent has " +
                "not subscribed by then the message resolves to zero consumers and is dropped " +
                "permanently, stranding the repetition and (since #198) holding the container's " +
                "completion forever. Bus trace: {0}",
                string.Join(" | ", harness.Bus.Log));
        }

        // The same property stated as bare call order, so the guard survives any future
        // simplification of FakeCaseEventBus.
        [Fact]
        public async Task CreateChild__Given_AnyChild__Then_BothChildSubscriptionsAreArmedBeforeDefineRepetitionAndTriggerCreate()
        {
            var harness = new CreateChildHarness();
            harness.ArrangeInertChild();

            await harness.CreateChild();

            // "The first two calls are the two subscribes, in either order" rather than the whole
            // log verbatim: the two subscribes are a Task.WhenAll, so their order relative to each
            // other is not a property worth pinning, and an exact-sequence assertion would break on
            // any legitimate call later added to CreateChild.
            harness.CallLog.Take(2).Should().BeEquivalentTo(
                new[]
                {
                    $"Subscribe({nameof(PlanItemTransitionedEvent)})",
                    $"Subscribe({nameof(PlanItemRepetitionCriteriaMetEvent)})"
                },
                "#181: both of the parent's child subscriptions must be established before the " +
                "child is defined or triggered - nothing the child's creation turn publishes may " +
                "be able to reach a stream this parent has not subscribed to yet");

            harness.CallLog.Should().ContainInOrder("DefineRepetition", "Trigger(Create)");
        }

        // #160/#181 - the partial-failure retry path, which RepetitionRedeliveryIntegrationTests
        // does not cover (that test covers redelivery AFTER a fully successful spawn).
        //
        // CreateChild raises ChildCreated only once every one of its grain calls has succeeded, so
        // a throw partway leaves nothing recorded, this handler's ConfirmEvents never runs, and the
        // stream agent's retry-then-redeliver re-runs the whole handler. The hoist moves the
        // subscribe to BEFORE the throwing calls, so a failed attempt now leaves a live
        // subscription behind with no child instance behind it. What is asserted here is exactly
        // that two-part state and no more - the subscription WAS armed, and NO ChildCreated was
        // recorded. That the leftover subscription is harmless follows from its being keyed on the
        // child's definition id and from SubscribeTo's Create idempotence (see CreateChild's
        // remarks); it is reasoning, not something these assertions demonstrate. The retry actually
        // succeeding is the next test's job.
        [Fact]
        public async Task CreateChild__When_TriggerCreateThrowsAfterTheSubscribe__Then_SubscriptionsAreArmedAndNoChildCreatedIsRaised()
        {
            var harness = new CreateChildHarness();
            harness.ArrangeChildThatThrowsOnTriggerCreate();

            Func<Task> act = () => harness.CreateChild();

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

            harness.Host.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never,
                "a partially-created child must not be recorded as created - that record is what " +
                "makes the stream agent's retry re-run CreateChild rather than silently no-op");

            harness.CallLog.Should().StartWith(new[]
            {
                $"Subscribe({nameof(PlanItemTransitionedEvent)})",
                $"Subscribe({nameof(PlanItemRepetitionCriteriaMetEvent)})"
            }, "the subscriptions are armed first, so they survive a failure of the calls after them");
        }

        // The other half of the partial-failure story, driven through the real handler rather than
        // CreateChild alone: the #161 redelivery guard (ChildRepeated) must NOT have been recorded
        // by the failed attempt, or the retry would recognise itself as a duplicate and silently
        // drop the repetition forever. Retrying the IDENTICAL event after the transient failure
        // clears must therefore spawn the child, and the two attempts together must produce exactly
        // one ChildCreated - the "at worst duplicates, never silently loses" property
        // SpawnRepetitionOrRefuseCeiling's remarks claim but nothing tested.
        [Fact]
        public async Task HandleChildRepeated__When_TheFirstAttemptFailsPartway__Then_RedeliveringTheSameEventSpawnsTheChildExactlyOnce()
        {
            var harness = new CreateChildHarness();
            harness.ArrangeChildThatThrowsOnTriggerCreate();

            var request = new PlanItemRepetitionCriteriaMetEvent(
                harness.Host.Object.Address,
                ShortGuid.NewGuid(),
                harness.Child.Id,
                currentRepetition: 0);

            Func<Task> firstAttempt = () => InvokeHandleChildRepeated(harness.Subject, request);
            await firstAttempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

            harness.Host.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never,
                "#160: the redelivery guard is recorded only AFTER CreateChild succeeds, so a " +
                "partial failure must leave the request retryable rather than self-suppressing");
            harness.Host.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);

            // the transient failure clears; the stream agent redelivers the identical event
            harness.ArrangeInertChild();

            await InvokeHandleChildRepeated(harness.Subject, request);

            harness.Host.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Once,
                "the retry must actually spawn the repetition the first attempt failed to");
            harness.Host.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Once,
                "and must record the guard exactly once, so any FURTHER redelivery is suppressed");

            harness.Store.BehaviorExtension.As<StageBehaviorStore>()
                .Children[harness.Child.Id].Should().HaveCount(1,
                    "exactly one physical child instance may exist for the retried request");
        }
    }
}
