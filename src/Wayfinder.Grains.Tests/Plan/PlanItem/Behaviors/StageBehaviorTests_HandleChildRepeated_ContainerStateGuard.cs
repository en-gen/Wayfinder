using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.PlanItem.Behaviors;
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
    // #178 - StageBehavior.HandleChildRepeated used to act on ANY PlanItemRepetitionCriteriaMetEvent
    // scoped to this Stage without ever consulting Host.State.PlanItemState, letting a Terminated
    // (or Suspended) container spawn a "zombie" repetition child. This file pins the fix's
    // container-liveness guard directly against the private handler, mirroring
    // StageBehaviorTests_HandleChildRepeated_RepetitionGuard.cs's harness (mocked IBehaviorHost, a
    // real backing PlanItemStateMachine via MockPlanItemStateMachine so Resume/ParentResume entry
    // actions genuinely fire). The Terminated-via-a-live-grain-cascade end-to-end scenario is
    // covered separately at the integration layer
    // (Plan/CasePlanModel/RepetitionAfterTerminationIntegrationTests.cs); everything reachable
    // through this handler in isolation belongs here, at the fast unit layer.
    public partial class StageBehaviorTests
    {
        private static readonly MethodInfo HandleChildRepeatedMethod = typeof(StageBehavior)
            .GetMethod("HandleChildRepeated", BindingFlags.NonPublic | BindingFlags.Instance);

        private static Task InvokeHandleChildRepeated(StageBehavior subject, PlanItemRepetitionCriteriaMetEvent @event) =>
            (Task)HandleChildRepeatedMethod.Invoke(subject, new object[] { @event, (StreamSequenceToken)null });

        // #178 - Table 8.9: a terminating Stage cascades exit to every non-terminal child before it
        // reaches a terminal state itself, so nothing live can remain to legitimately request a
        // repetition. All three genuinely terminal states must refuse identically: no spawn, no
        // buffered entry, no new domain event beyond the log line.
        [Theory]
        [InlineData(PlanItemState.Completed)]
        [InlineData(PlanItemState.Terminated)]
        [InlineData(PlanItemState.Closed)]
        public async Task HandleChildRepeated__Given_HostStateTerminal__Then_RefuseSpawnAndRaiseNoEvent(PlanItemState terminalState)
        {
            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: terminalState);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, ShortGuid.NewGuid(), planItemDefinitionId, 0));

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionBuffered>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionCeilingExceeded>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionRefusedWhileFailed>()), Times.Never);

            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions.Should().BeEmpty();
            testStore.PlanItemState.Should().Be(terminalState, "the refused spawn attempt must not itself change this Host's state");
        }

        // #178 - Failed is semi-terminal and re-activatable, unlike the genuinely terminal states
        // above, so it gets its own branch: refuse the spawn (same as terminal), but raise an
        // OBSERVABLE event rather than only a log line, so the refusal is visible to whoever
        // investigates/recovers the Failed case.
        [Fact]
        public async Task HandleChildRepeated__Given_HostStateFailed__Then_RefuseSpawnAndRaiseObservableEvent()
        {
            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();
            var sourceInstanceId = ShortGuid.NewGuid();
            const int currentRepetition = 4;

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Failed);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, sourceInstanceId, planItemDefinitionId, currentRepetition));

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionBuffered>()), Times.Never);

            mockHost.Verify(x => x.RaiseEvent(It.Is<RepetitionRefusedWhileFailed>(e =>
                e.RepeatingPlanItemDefinitionId == planItemDefinitionId &&
                e.SourceInstanceId == sourceInstanceId &&
                e.AttemptedRepetition == currentRepetition + 1)), Times.Once);

            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions.Should().BeEmpty();
            testStore.PlanItemState.Should().Be(PlanItemState.Failed);
        }

        // #178 - docs/03-cmmn-execution-semantics.md section 2 / Table 8.9 + Figure 8.3: no
        // repetition trigger can legitimately originate inside a genuinely Suspended Stage, so an
        // event observed here was earned before suspension and merely late. It must be BUFFERED,
        // not dropped and not spawned immediately.
        [Fact]
        public async Task HandleChildRepeated__Given_HostStateSuspended__Then_BufferInsteadOfSpawning()
        {
            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();
            var sourceInstanceId = ShortGuid.NewGuid();
            const int currentRepetition = 1;

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Suspended);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, sourceInstanceId, planItemDefinitionId, currentRepetition));

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never);

            mockHost.Verify(x => x.RaiseEvent(It.Is<RepetitionBuffered>(e =>
                e.SourceInstanceId == sourceInstanceId &&
                e.PlanItemDefinitionId == planItemDefinitionId &&
                e.NextRepetition == currentRepetition + 1)), Times.Once);

            var stageStore = (StageBehaviorStore)testStore.BehaviorExtension;
            stageStore.PendingRepetitions.Should().HaveCount(1);
            stageStore.HasPendingRepetition(sourceInstanceId).Should().BeTrue();
            testStore.PlanItemState.Should().Be(PlanItemState.Suspended, "buffering must not itself change this Host's state");
        }

        // #178 hazard 1 - the #161 redelivery guard only records once a child is ACTUALLY created
        // (CreateChild succeeds), so while Suspended a redelivered event would otherwise buffer a
        // SECOND copy. The buffer must dedupe on the same SourceInstanceId.
        [Fact]
        public async Task HandleChildRepeated__Given_RedeliveredWhileSuspended__Then_DoesNotDoubleBuffer()
        {
            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();
            var sourceInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Suspended);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            var repetitionEvent = new PlanItemRepetitionCriteriaMetEvent(address, sourceInstanceId, planItemDefinitionId, 0);

            await InvokeHandleChildRepeated(subject, repetitionEvent);
            await InvokeHandleChildRepeated(subject, repetitionEvent);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionBuffered>()), Times.Once,
                "a redelivered event while still Suspended must not queue a second buffered entry");

            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions.Should().HaveCount(1);
        }

        // #178 - the buffer must actually replay once the Stage returns to Active. Resume is
        // registered as an entry action on Active in StageBehavior's constructor
        // (DrainPendingRepetitions); firing it through the REAL backing PlanItemStateMachine (not
        // just asserting the buffer's contents) proves the wiring, not just the store.
        [Fact]
        public async Task Resume__Given_BufferedRepetitionFromWhileSuspended__Then_DrainsAndSpawnsChild()
        {
            const int ceiling = 10;
            const int currentRepetition = 1;

            var caseInstanceId = Guid.NewGuid();
            var parentInstanceId = ShortGuid.NewGuid();
            var instanceId = ShortGuid.NewGuid();
            var scope = $"CPM.{parentInstanceId}";
            var address = $"{scope}.{instanceId}";
            var planItemDefinitionId = ShortGuid.NewGuid();
            var definitionScope = $"CPM.{ShortGuid.NewGuid()}";
            var sourceInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Suspended);

            var mockPlanItemGrain = new Mock<IPlanItemInternalGrain>();

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, It.IsAny<string>(), null))
                .Returns(mockPlanItemGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.ParentInstanceId).Returns(parentInstanceId);
            mockHost.Setup(x => x.DefinitionId).Returns(stage.Id);
            mockHost.Setup(x => x.DefinitionScope).Returns(definitionScope);
            mockHost.Setup(x => x.InstanceId).Returns(instanceId);
            mockHost.Setup(x => x.Scope).Returns(scope);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object, ceiling);

            // Buffer the request while genuinely Suspended (real handler, not a direct store call).
            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, sourceInstanceId, planItemDefinitionId, currentRepetition));

            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions.Should().HaveCount(1,
                "the request must have been buffered, not spawned, while Suspended");

            // Resume -> Active through the REAL PlanItemStateMachine, which runs
            // StageBehavior's registered DrainPendingRepetitions entry action.
            await mockMachine.Object.FireAsync(PlanItemTransition.Resume);

            testStore.PlanItemState.Should().Be(PlanItemState.Active);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionCeilingExceeded>()), Times.Never);

            mockPlanItemGrain.Verify(x => x.DefineRepetition(
                    testStore.CaseDefinitionId, pi, currentRepetition + 1, stage.Id, definitionScope),
                Times.Once);
            mockPlanItemGrain.Verify(x => x.Trigger(PlanItemTransition.Create), Times.Once);

            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions.Should().BeEmpty(
                "a drained entry (spawned or refused) must be removed from the buffer");
        }

        // #178 hazard 2 - draining the buffer must go through the SAME #67
        // RepetitionGuardOptions.MaxRepetitionsPerPlanItem check a live request would - a buffered
        // request must not bypass the ceiling just because it arrived via replay instead of live.
        [Fact]
        public async Task Resume__Given_BufferedRepetitionAtCeiling__Then_RefusesSpawnAndFaultsHost()
        {
            const int ceiling = 3;
            const int currentRepetition = 2; // nextRepetition = 3 == ceiling - must be refused

            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();
            var sourceInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Suspended);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object, ceiling);

            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, sourceInstanceId, planItemDefinitionId, currentRepetition));

            await mockMachine.Object.FireAsync(PlanItemTransition.Resume);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.Is<RepetitionCeilingExceeded>(e =>
                e.RepeatingPlanItemDefinitionId == planItemDefinitionId &&
                e.AttemptedRepetition == ceiling &&
                e.Ceiling == ceiling)), Times.Once);

            testStore.PlanItemState.Should().Be(PlanItemState.Failed,
                "the ceiling must still Fault the container even when reached via a buffered replay");

            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions.Should().BeEmpty(
                "a ceiling-refused buffered entry is drained (not retried), matching the live ceiling path's own no-retry semantics");
        }

        // #178 self-review hazard - "can a buffered request be replayed into a Stage that has
        // since [become unable to host it]?" With two buffered entries, the first hitting the
        // ceiling Faults the Host mid-drain; DrainPendingRepetitions must re-check state on every
        // iteration and stop, leaving the SECOND entry buffered and never even attempted - not
        // spawned into (or refused-and-re-faulted-within) a container that is no longer Active.
        [Fact]
        public async Task Resume__Given_TwoBufferedRepetitions__When_FirstHitsCeiling__Then_SecondStaysBufferedAndUnprocessed()
        {
            const int ceiling = 2;

            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();
            var firstSourceInstanceId = ShortGuid.NewGuid();
            var secondSourceInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Suspended);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object, ceiling);

            // Both requests arrive while Suspended, from two different source instances - the
            // first (currentRepetition=1 -> nextRepetition=2 == ceiling) will be refused on drain;
            // the second (currentRepetition=5, arbitrary) must never even be attempted.
            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, firstSourceInstanceId, planItemDefinitionId, 1));
            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, secondSourceInstanceId, planItemDefinitionId, 5));

            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions.Should().HaveCount(2);

            await mockMachine.Object.FireAsync(PlanItemTransition.Resume);

            testStore.PlanItemState.Should().Be(PlanItemState.Failed);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionCeilingExceeded>()), Times.Once,
                "only the FIRST buffered entry should ever be attempted once the Host Faults mid-drain");
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);

            var remaining = ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions;
            remaining.Should().HaveCount(1, "the drain must stop re-checking state and leave the untried entry buffered");
            remaining.Single().SourceInstanceId.Should().Be(secondSourceInstanceId);
        }
    }
}
