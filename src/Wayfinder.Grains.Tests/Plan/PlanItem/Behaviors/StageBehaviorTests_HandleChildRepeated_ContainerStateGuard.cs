using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;
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
        // buffered entry, no ceiling/Failed audit event.
        //
        // #198 - the refusal DOES journal one thing now: RepetitionRequestSettled, the record that
        // this container has definitively dealt with the request. Without it, a child that reached
        // a terminal state with Repeated set would keep blocking this container's Table 8.12
        // completion forever (see StageBehavior.RepetitionRequestsAwaitingResolution and
        // StageBehaviorTests_RepetitionCompletionGate.cs).
        [Theory]
        [InlineData(PlanItemState.Completed)]
        [InlineData(PlanItemState.Terminated)]
        [InlineData(PlanItemState.Closed)]
        public async Task HandleChildRepeated__Given_HostStateTerminal__Then_RefuseSpawnAndSettleTheRequest(PlanItemState terminalState)
        {
            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();
            var sourceInstanceId = ShortGuid.NewGuid();

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
                address, sourceInstanceId, planItemDefinitionId, 0));

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionBuffered>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionCeilingExceeded>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionRefusedWhileFailed>()), Times.Never);

            mockHost.Verify(x => x.RaiseEvent(It.Is<RepetitionRequestSettled>(e =>
                e.SourceInstanceId == sourceInstanceId)), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once);

            var stageStore = (StageBehaviorStore)testStore.BehaviorExtension;
            stageStore.PendingRepetitions.Should().BeEmpty();
            stageStore.IsRepetitionSettled(sourceInstanceId).Should().BeTrue();
            testStore.PlanItemState.Should().Be(terminalState, "the refused spawn attempt must not itself change this Host's state");
        }

        // #198 - the unknown-child branch is also a definitive refusal: this container cannot even
        // identify what to spawn, so nothing ever will be, and the request must stop blocking
        // completion.
        [Fact]
        public async Task HandleChildRepeated__Given_UnknownChildDefinition__Then_SettleTheRequest()
        {
            var address = ShortGuid.NewGuid();
            var sourceInstanceId = ShortGuid.NewGuid();

            var stage = new Stage { PlanItems = { new Interfaces.Model.PlanItem { Id = ShortGuid.NewGuid() } } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Active);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, sourceInstanceId, "not_a_known_child", 0));

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never);

            ((StageBehaviorStore)testStore.BehaviorExtension)
                .IsRepetitionSettled(sourceInstanceId).Should().BeTrue();
        }

        // #178 - Failed is semi-terminal and re-activatable, unlike the genuinely terminal states
        // above, so it gets its own branch: refuse the spawn (same as terminal), but raise an
        // OBSERVABLE event rather than only a log line, so the refusal is visible to whoever
        // investigates/recovers the Failed case.
        [Fact]
        public async Task HandleChildRepeated__Given_HostStateFailed__Then_RefuseSpawnAndRaiseObservableEvent()
        {
            var address = ShortGuid.NewGuid();
            // The child PlanItem's own Id (not a PlanItemDefinition/DefinitionRef id) - it feeds
            // both the fixture's PlanItem.Id and the event's SourceDefinitionId below, mirroring
            // production (Host.DefinitionId => Definition.Id is that same own-id, per
            // PlanItemGrain's IBehaviorHost.DefinitionId remarks), so RepetitionRefusedWhileFailed
            // is expected to carry this same value back out as RepeatingPlanItemId.
            var planItemId = ShortGuid.NewGuid();
            var sourceInstanceId = ShortGuid.NewGuid();
            const int currentRepetition = 4;

            var pi = new Interfaces.Model.PlanItem { Id = planItemId };
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
                address, sourceInstanceId, planItemId, currentRepetition));

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionBuffered>()), Times.Never);

            mockHost.Verify(x => x.RaiseEvent(It.Is<RepetitionRefusedWhileFailed>(e =>
                e.RepeatingPlanItemId == planItemId &&
                e.SourceInstanceId == sourceInstanceId &&
                e.AttemptedRepetition == currentRepetition + 1)), Times.Once);

            // #198 - a Failed container refuses definitively (this class's Reactivate registration
            // deliberately does not replay refused requests), so the request must also be recorded
            // as settled or it would block Table 8.12 completion forever after a recovery.
            mockHost.Verify(x => x.RaiseEvent(It.Is<RepetitionRequestSettled>(e =>
                e.SourceInstanceId == sourceInstanceId)), Times.Once);

            var failedStageStore = (StageBehaviorStore)testStore.BehaviorExtension;
            failedStageStore.PendingRepetitions.Should().BeEmpty();
            failedStageStore.IsRepetitionSettled(sourceInstanceId).Should().BeTrue();
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

            // #198 - the ONE branch that must NOT settle: the request is real and still pending,
            // so it has to keep blocking this container's Table 8.12 completion until
            // DrainPendingRepetitions spawns or ceiling-refuses it on resume.
            stageStore.IsRepetitionSettled(sourceInstanceId).Should().BeFalse(
                "a buffered request is still owed a successor - blocking completion here is correct");

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

        // #178 - the buffer must actually replay once the Stage returns to Active, via EITHER
        // trigger that lands there from Suspended (review round 2, item 7 - both Resume and
        // ParentResume are wired to DrainPendingRepetitions in the constructor, but only Resume
        // was ever exercised; the sibling trigger was the exact "one path wired, one forgotten"
        // shape that produced the CasePlanModel Reactivate blocker below, so it gets equal
        // coverage here). ParentResume needs a genuine ParentSuspend cascade first (not just
        // Suspended as the initial state) so PlanItemStateMachine's ParentSuspendState -
        // PermitDynamicIf(ParentResume, ...) needs a real value to dynamically resolve back to
        // Active. Firing through the REAL backing PlanItemStateMachine (not just asserting the
        // buffer's contents) proves the wiring, not just the store.
        [Theory]
        [InlineData(PlanItemTransition.Suspend, PlanItemTransition.Resume)]
        [InlineData(PlanItemTransition.ParentSuspend, PlanItemTransition.ParentResume)]
        public async Task Resume__Given_BufferedRepetitionFromWhileSuspended__Then_DrainsAndSpawnsChild(
            PlanItemTransition suspendTrigger, PlanItemTransition resumeTrigger)
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

            // Starts Active (not Suspended directly) so suspendTrigger's own transition genuinely
            // runs and, for the ParentSuspend case, actually populates ParentSuspendState.
            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Active);

            var mockPlanItemGrain = new Mock<IPlanItemInternalGrain>();
            StubFreshlySpawnedChildSnapshot(mockPlanItemGrain);

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

            await mockMachine.Object.FireAsync(suspendTrigger);
            testStore.PlanItemState.Should().Be(PlanItemState.Suspended);

            // Buffer the request while genuinely Suspended (real handler, not a direct store call).
            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, sourceInstanceId, planItemDefinitionId, currentRepetition));

            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions.Should().HaveCount(1,
                "the request must have been buffered, not spawned, while Suspended");

            // Resume/ParentResume -> Active through the REAL PlanItemStateMachine, which runs
            // StageBehavior's registered DrainPendingRepetitions entry action.
            await mockMachine.Object.FireAsync(resumeTrigger);

            testStore.PlanItemState.Should().Be(PlanItemState.Active);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionCeilingExceeded>()), Times.Never);

            mockPlanItemGrain.Verify(x => x.DefineRepetition(
                    testStore.CaseDefinitionId, pi, currentRepetition + 1, stage.Id, definitionScope),
                Times.Once);
            mockPlanItemGrain.Verify(x => x.Trigger(PlanItemTransition.Create), Times.Once);

            var drainedStore = (StageBehaviorStore)testStore.BehaviorExtension;
            drainedStore.PendingRepetitions.Should().BeEmpty(
                "a drained entry (spawned or refused) must be removed from the buffer");

            // #198 - the buffered-then-drained path completes the settle lifecycle: the request
            // blocked Table 8.12 completion while it sat in the buffer, and the spawn's
            // ChildRepeated (#161) is what finally resolves it.
            drainedStore.IsRepetitionSettled(sourceInstanceId).Should().BeTrue(
                "spawning the successor resolves the request the buffer was holding");
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
                e.RepeatingPlanItemId == planItemDefinitionId &&
                e.AttemptedRepetition == ceiling &&
                e.Ceiling == ceiling)), Times.Once);

            testStore.PlanItemState.Should().Be(PlanItemState.Failed,
                "the ceiling must still Fault the container even when reached via a buffered replay");

            var ceilingStore = (StageBehaviorStore)testStore.BehaviorExtension;
            ceilingStore.PendingRepetitions.Should().BeEmpty(
                "a ceiling-refused buffered entry is drained (not retried), matching the live ceiling path's own no-retry semantics");

            // #198 - a ceiling refusal is final, so it must settle the request. Otherwise the
            // repeating child - terminal, Repeated, never spawned - would block this container's
            // Table 8.12 completion permanently, with no operator recourse but Terminate.
            ceilingStore.IsRepetitionSettled(sourceInstanceId).Should().BeTrue();
            // ...but the #161 redelivery guard is deliberately NOT recorded by the ceiling branch
            // (see SpawnRepetitionOrRefuseCeiling's scope note) - the two records mean different
            // things, and this pins that they have not been conflated.
            ceilingStore.IsRepetitionRedelivery(sourceInstanceId).Should().BeFalse();
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

            var strandedStore = (StageBehaviorStore)testStore.BehaviorExtension;
            var remaining = strandedStore.PendingRepetitions;
            remaining.Should().HaveCount(1, "the drain must stop re-checking state and leave the untried entry buffered");
            remaining.Single().SourceInstanceId.Should().Be(secondSourceInstanceId);

            // #198 F2 - ...but "untried" must not mean "wedging". Nothing replays this entry after a
            // Failed->Reactivate (neither StageBehavior nor CasePlanModelBehavior drains on that
            // recovery, both deliberately), so if it stayed UNSETTLED its requesting child would be
            // terminal + Repeated + unresolved forever and Table 8.12 completion would be
            // permanently unavailable on the reactivated container. Settled, therefore, without
            // being attempted or drained - see StageBehavior.SettleStrandedPendingRepetitions.
            strandedStore.IsRepetitionSettled(secondSourceInstanceId).Should().BeTrue(
                "an entry stranded behind a ceiling breach must stop holding Table 8.12 completion");
            strandedStore.IsRepetitionRedelivery(secondSourceInstanceId).Should().BeFalse(
                "settling it says nothing about a child having been spawned - no child ever was");
        }

        // #178 review round 2, BLOCKER - StageBehavior's own constructor only wires
        // DrainPendingRepetitions to Resume/ParentResume, but ConfigureForCasePlanModel permits
        // ONLY Reactivate + Close out of Suspended (Table 8.6 - the Case has no Resume/
        // ParentResume edge at all). Without CasePlanModelBehavior's own Reactivate registration
        // (guarded to transition.Source == Suspended), a repetition request buffered while the
        // CasePlanModel itself was genuinely Suspended would never drain - this proves it does,
        // through the real CasePlanModel-shaped state machine (Stage.IsCasePlanModel = true, same
        // harness shape RepeatOnCompleteOrTerminateTests already establishes for
        // CasePlanModelBehavior).
        [Fact]
        public async Task Reactivate__Given_CasePlanModelBufferedRepetitionWhileSuspended__Then_DrainsAndSpawnsChild()
        {
            const int ceiling = 10;
            const int currentRepetition = 1;

            var caseInstanceId = Guid.NewGuid();
            var instanceId = "CPM";
            var planItemDefinitionId = ShortGuid.NewGuid();
            var definitionScope = "CPM";
            var sourceInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var casePlanModel = new Stage { Id = "CPM", IsCasePlanModel = true, PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: casePlanModel, initialState: PlanItemState.Active);

            var mockPlanItemGrain = new Mock<IPlanItemInternalGrain>();
            StubFreshlySpawnedChildSnapshot(mockPlanItemGrain);

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, It.IsAny<string>(), null))
                .Returns(mockPlanItemGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.Address).Returns(instanceId);
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.DefinitionId).Returns(casePlanModel.Id);
            mockHost.Setup(x => x.DefinitionScope).Returns(definitionScope);
            mockHost.Setup(x => x.InstanceId).Returns(instanceId);
            mockHost.Setup(x => x.Scope).Returns(string.Empty);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new CasePlanModelBehavior(mockHost.Object, casePlanModel, mockMachine.Object, ceiling);

            // Table 8.6 - the Case's own Suspend (not a cascaded ParentSuspend - the CasePlanModel
            // has no parent) takes it Active -> Suspended.
            await mockMachine.Object.FireAsync(PlanItemTransition.Suspend);
            testStore.PlanItemState.Should().Be(PlanItemState.Suspended);

            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                instanceId, sourceInstanceId, planItemDefinitionId, currentRepetition));

            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions.Should().HaveCount(1,
                "the request must have been buffered, not spawned, while the CasePlanModel was Suspended");

            // Table 8.6 - the Case's own Reactivate (Suspended -> Active). This is the SAME
            // trigger name used from Completed/Terminated/Failed, but CasePlanModelBehavior's
            // registration only drains when the source was genuinely Suspended.
            await mockMachine.Object.FireAsync(PlanItemTransition.Reactivate);

            testStore.PlanItemState.Should().Be(PlanItemState.Active);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Once);

            mockPlanItemGrain.Verify(x => x.DefineRepetition(
                    testStore.CaseDefinitionId, pi, currentRepetition + 1, casePlanModel.Id, definitionScope),
                Times.Once);
            mockPlanItemGrain.Verify(x => x.Trigger(PlanItemTransition.Create), Times.Once);

            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions.Should().BeEmpty();
        }

        // #178 review round 2, HIGH - the drain must honor the SAME #161 redelivery guard the
        // live path checks. Simulates the race directly: a child was already spawned for this
        // source instance (the durable guard is recorded) but the buffer entry that requested it
        // was never removed (the process died before RepetitionBufferDrained was confirmed) -
        // draining must recognize this as already-satisfied and clean up the stale entry WITHOUT
        // spawning a second child.
        [Fact]
        public async Task DrainPendingRepetitions__Given_EntryAlreadyGuardedByRedeliveryCheck__Then_SkipsAndDrainsWithoutSpawning()
        {
            const int ceiling = 10;
            const int currentRepetition = 1;

            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();
            var sourceInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Active);

            var mockGrainFactory = new Mock<IGrainFactory>();

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object, ceiling);

            await mockMachine.Object.FireAsync(PlanItemTransition.Suspend);

            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, sourceInstanceId, planItemDefinitionId, currentRepetition));

            var stageStore = (StageBehaviorStore)testStore.BehaviorExtension;
            stageStore.PendingRepetitions.Should().HaveCount(1);

            // Simulate: an earlier drain already spawned the child (durably recording the #161
            // guard via ChildRepeated) but crashed before confirming the RepetitionBufferDrained
            // that would have removed this now-stale entry.
            stageStore.Apply(new ChildRepeated { SourceInstanceId = sourceInstanceId });
            stageStore.IsRepetitionRedelivery(sourceInstanceId).Should().BeTrue();

            await mockMachine.Object.FireAsync(PlanItemTransition.Resume);

            // No grain call at all - GetGrain must never even be reached for this entry.
            mockGrainFactory.Verify(x => x.GetGrain<IPlanItemInternalGrain>(It.IsAny<Guid>(), It.IsAny<string>(), null), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never,
                "the guard was already recorded by the simulated earlier drain - this pass must not raise it again");

            stageStore.PendingRepetitions.Should().BeEmpty(
                "the stale buffered entry must still be drained (removed) even though it was skipped, not spawned");
        }

        // #178 review round 2, SHOULD-FIX - DrainPendingRepetitions runs as an entry action of the
        // transition itself still being dispatched; an exception escaping the loop would escape
        // FireAsync(Resume) entirely and permanently strand the whole batch (no stream-agent
        // redelivery exists for the buffered path, unlike the live one). A transient failure on
        // one buffered entry must leave THAT entry buffered and let the rest of the batch proceed.
        [Fact]
        public async Task DrainPendingRepetitions__Given_FirstEntryThrows__Then_LeavesItBufferedAndProcessesSecondEntry()
        {
            const int ceiling = 10;

            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();
            var definitionScope = $"CPM.{ShortGuid.NewGuid()}";
            var throwingSourceInstanceId = ShortGuid.NewGuid();
            var succeedingSourceInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Active);

            var mockPlanItemGrain = new Mock<IPlanItemInternalGrain>();
            StubFreshlySpawnedChildSnapshot(mockPlanItemGrain);
            // First entry drained (FIFO - the throwing one, buffered first below) throws; the
            // second entry's Trigger(Create) call (this same mock, a fresh grain key per
            // CreateChild call but this SetupSequence applies across ALL calls in order) succeeds.
            mockPlanItemGrain.SetupSequence(x => x.Trigger(PlanItemTransition.Create))
                .ThrowsAsync(new TimeoutException("transient failure"))
                .ReturnsAsync((PlanItemSnapshot)null);

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, It.IsAny<string>(), null))
                .Returns(mockPlanItemGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.DefinitionId).Returns(stage.Id);
            mockHost.Setup(x => x.DefinitionScope).Returns(definitionScope);
            mockHost.Setup(x => x.Scope).Returns("CPM");
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object, ceiling);

            await mockMachine.Object.FireAsync(PlanItemTransition.Suspend);

            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, throwingSourceInstanceId, planItemDefinitionId, 1));
            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, succeedingSourceInstanceId, planItemDefinitionId, 2));

            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions.Should().HaveCount(2);

            // Must not throw out of the transition itself - the whole point of catching inside
            // the loop.
            Func<Task> act = () => mockMachine.Object.FireAsync(PlanItemTransition.Resume);
            await act.Should().NotThrowAsync();

            testStore.PlanItemState.Should().Be(PlanItemState.Active,
                "the transition itself must complete normally despite one buffered entry throwing");

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Once,
                "only the SECOND (succeeding) entry should have spawned a child");
            mockHost.Verify(x => x.RaiseEvent(It.Is<ChildRepeated>(e => e.SourceInstanceId == succeedingSourceInstanceId)), Times.Once);

            var remaining = ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions;
            remaining.Should().HaveCount(1, "the throwing entry must remain buffered for a future drain attempt");
            remaining.Single().SourceInstanceId.Should().Be(throwingSourceInstanceId);
        }

        // #216 phase 1 (characterization) - StageBehavior's constructor argues at length (see its
        // Resume/ParentResume registration remarks) that an ordinary Stage deliberately gets NO
        // DrainPendingRepetitions hook on Reactivate: for a Stage/Task, Reactivate's only source is
        // Failed, and an entry left buffered after the #67 ceiling faulted this Host mid-drain is,
        // by construction, one that would immediately re-fault the container if replayed. A human
        // reactivating from Failed is recovering from whatever ORIGINALLY faulted the Host, not
        // asking to re-attempt every stale spawn queued behind it.
        //
        // That decision was argued in prose and pinned nowhere - the sibling decision it is
        // contrasted against (CasePlanModelBehavior's source-state-guarded Reactivate hook) has a
        // test above, but this one, the deliberate ABSENCE, had none. It is exactly the shape a
        // consolidation into BaseBehavior would "fix" by accident, by registering the drain hook on
        // every trigger that reaches Active.
        //
        // Reaches the setup the way production does rather than by seeding the store: two buffered
        // entries, the first breaching the ceiling on drain and faulting the Host, the second left
        // buffered and unattempted (the `break` in DrainPendingRepetitions - pinned by
        // Resume__Given_TwoBufferedRepetitions__… above). Its nextRepetition is deliberately well
        // UNDER the ceiling, so a Reactivate that did drain would genuinely spawn a child rather
        // than merely hit the same ceiling refusal again.
        [Fact]
        public async Task Reactivate__Given_OrdinaryStageWithBufferedRepetitionFromFailed__Then_DoesNotDrain()
        {
            const int ceiling = 3;

            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();
            var breachingSourceInstanceId = ShortGuid.NewGuid();
            var strandedSourceInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Suspended);

            var mockPlanItemGrain = new Mock<IPlanItemInternalGrain>();
            StubFreshlySpawnedChildSnapshot(mockPlanItemGrain);

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, It.IsAny<string>(), null))
                .Returns(mockPlanItemGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.Address).Returns(address);
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.DefinitionId).Returns(stage.Id);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object, ceiling);

            // currentRepetition=2 -> nextRepetition=3 == ceiling: refused on drain, faults the Host.
            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, breachingSourceInstanceId, planItemDefinitionId, 2));
            // currentRepetition=0 -> nextRepetition=1, comfortably under the ceiling: this one WOULD
            // spawn if anything ever drained it.
            await InvokeHandleChildRepeated(subject, new PlanItemRepetitionCriteriaMetEvent(
                address, strandedSourceInstanceId, planItemDefinitionId, 0));

            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions.Should().HaveCount(2);

            await mockMachine.Object.FireAsync(PlanItemTransition.Resume);

            testStore.PlanItemState.Should().Be(PlanItemState.Failed,
                "the first buffered entry breaches the #67 ceiling on drain and faults this container");
            ((StageBehaviorStore)testStore.BehaviorExtension).PendingRepetitions
                .Should().ContainSingle(entry => entry.SourceInstanceId == strandedSourceInstanceId,
                    "the drain must have stopped at the ceiling breach, stranding the second entry");

            // Table 8.7/8.8 recovery: a human reactivates the Failed container.
            await mockMachine.Object.FireAsync(PlanItemTransition.Reactivate);

            testStore.PlanItemState.Should().Be(PlanItemState.Active,
                "Reactivate itself must still work - this test is about what it does NOT do");

            mockGrainFactory.Verify(
                x => x.GetGrain<IPlanItemInternalGrain>(It.IsAny<Guid>(), It.IsAny<string>(), null), Times.Never,
                "an ordinary Stage has no DrainPendingRepetitions hook on Reactivate - reactivating from Failed must not re-attempt the stale spawn that was queued behind the one that faulted it");
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never);

            var afterReactivate = (StageBehaviorStore)testStore.BehaviorExtension;
            afterReactivate.PendingRepetitions
                .Should().ContainSingle(entry => entry.SourceInstanceId == strandedSourceInstanceId,
                    "the stranded entry stays buffered and untouched across a Failed -> Reactivate recovery");

            // #198 F2 - it stays buffered, but it must NOT still be holding Table 8.12 completion
            // on the recovered container: SettleStrandedPendingRepetitions settled it where it was
            // stranded. Asserted here so "does not drain" can never be satisfied by re-introducing
            // the wedge that decision was paired with.
            afterReactivate.IsRepetitionSettled(strandedSourceInstanceId).Should().BeTrue();
        }
    }
}
