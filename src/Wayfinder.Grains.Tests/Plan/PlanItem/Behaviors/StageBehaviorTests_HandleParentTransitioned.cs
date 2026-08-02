using System.Reflection;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Moq;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Plan.PlanItem.Behaviors
{
    public partial class StageBehaviorTests
    {
        [Fact]
        public async Task HandleParentTransitioned__When_ParentInstanceIdEventSource__Then_Disregard()
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        "not_a_match",
                        "ParentStage",
                        PlanItemTransition.ParentSuspend,
                        PlanItemState.Active,
                        PlanItemState.Suspended),
                    (StreamSequenceToken)null
                });

            mockMachine.Object.State.Should().Be(PlanItemState.Available);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<object>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);
        }

        [Theory]
        [InlineData(PlanItemTransition.Suspend)]
        [InlineData(PlanItemTransition.ParentSuspend)]
        public async Task HandleParentTransitioned__When_ParentSuspended__Then_RaiseEventAndTransition
            (PlanItemTransition parentTransition)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        parentTransition,
                        PlanItemState.Active,
                        PlanItemState.Suspended),
                    (StreamSequenceToken)null
                });

            mockMachine.Object.State.Should().Be(PlanItemState.Suspended);
            mockMachine.Object.ParentSuspendState.Should().Be(PlanItemState.Available);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentSuspended>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.ParentSuspend), Times.Once);
        }

        [Theory]
        [InlineData(PlanItemTransition.Resume)]
        [InlineData(PlanItemTransition.ParentResume)]
        public async Task HandleParentTransitioned__When_ParentResumed__Then_ResumeToPreviousState
            (PlanItemTransition parentTransition)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            // we must ParentSuspend before we can ParentResume
            await (Task)typeof(StageBehavior)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        PlanItemTransition.ParentSuspend,
                        PlanItemState.Active,
                        PlanItemState.Suspended),
                    (StreamSequenceToken)null
                });

            mockMachine.Object.State.Should().Be(PlanItemState.Suspended);
            mockMachine.Object.ParentSuspendState.Should().Be(PlanItemState.Available);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        parentTransition,
                        PlanItemState.Suspended,
                        PlanItemState.Active),
                    (StreamSequenceToken)null
                });

            mockMachine.Object.ParentSuspendState.Should().BeNull();
            mockMachine.Object.State.Should().Be(PlanItemState.Available);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentResumed>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.ParentResume), Times.Once);
        }

        [Theory]
        [InlineData(PlanItemTransition.Exit)]
        [InlineData(PlanItemTransition.Terminate)]
        public async Task HandleParentTransitioned__When_ParentTerminated__Then_Exit
            (PlanItemTransition parentTransition)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage, def: pi);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        parentTransition,
                        PlanItemState.Active,
                        PlanItemState.Suspended),
                    (StreamSequenceToken)null
                });

            mockMachine.Object.ParentSuspendState.Should().BeNull();
            mockMachine.Object.State.Should().Be(PlanItemState.Terminated);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentTerminated>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Exit), Times.Once);
        }

        // #216 phase 1 (characterization) - #179's `complete` cascade had NO unit coverage on
        // either side; the only thing pinning it was one integration scenario.
        //
        // WHAT THE TABLE SAYS. Table 8.9's `complete` rows, Stage/Task column: a child in
        // {Available, Enabled, Active, Suspended} has Transition = N/A and To state =
        // `<impossible>`. The table asserts the combination cannot arise. It does NOT prescribe a
        // transition - N/A means exactly that, and the `exit, terminate` rows a few lines below it
        // are where the table does name `exit`.
        //
        // WHAT THIS REPO INFERS. Table 8.12's autoComplete=TRUE criteria ("no Active children AND
        // all REQUIRED children terminal") say nothing about non-required children, so a Stage CAN
        // legitimately reach Completed with a non-required child still Available/Enabled - which is
        // the `<impossible>` combination. #179 reconciles the two tables by having completion drive
        // that remainder out via `exit`, reusing the existing downward cascade. That is a design
        // decision, not a quotation.
        //
        // So a future author is free to reconcile them differently - firing `terminate` instead, or
        // changing Table 8.12 evaluation so the Stage does not complete at all while a non-required
        // child is still live. Either would turn this test red, and that red would be a DESIGN
        // DISAGREEMENT to discuss, not a spec violation. What is NOT open to reinterpretation is
        // the sibling test below (already-terminal children are left alone, which the table states
        // outright) and the Milestone/EventListener asymmetry in MilestoneBehaviorTests /
        // EventListenerBehaviorTests - those are the table's own cells.
        //
        // See StageBehavior.HandleParentTransitioned's Complete case.
        //
        // The mock host needs a real backing store here (unlike this file's other tests): the
        // Complete arm reads Host.State.PlanItemState.IsTerminal(), which NREs against a bare
        // Mock<IBehaviorHost>.
        [Theory]
        [InlineData(PlanItemState.Available)]
        [InlineData(PlanItemState.Enabled)]
        [InlineData(PlanItemState.Active)]
        [InlineData(PlanItemState.Suspended)]
        public async Task HandleParentTransitioned__When_ParentCompleted__Then_RaiseParentCompletedAndExit
            (PlanItemState hostState)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: hostState);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        PlanItemTransition.Complete,
                        PlanItemState.Active,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentCompleted>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Exit), Times.Once);

            mockMachine.Object.State.Should().Be(PlanItemState.Terminated,
                "Table 8.9 marks a non-terminal Stage child of a Completed parent `<impossible>` (Transition = N/A); #179 reconciles that with Table 8.12 by cascading exit, which is this repo's design decision rather than the table's own prescription - see this test's remarks");
        }

        // #216 phase 1 (characterization) - the other half of Table 8.9's `complete` rows:
        // {Disabled, Completed, Terminated, Failed} MAY coexist with a Completed parent, which is
        // exactly PlanItemState.IsTerminal(). This matters because ConfigureForStageOrTask permits
        // Exit from Disabled and Failed too, so a bare CanFire(Exit) gate would wrongly cascade
        // into both - the Complete arm needs its own, narrower IsTerminal() condition and this
        // pins that it has one.
        [Theory]
        [InlineData(PlanItemState.Disabled)]
        [InlineData(PlanItemState.Failed)]
        [InlineData(PlanItemState.Completed)]
        [InlineData(PlanItemState.Terminated)]
        public async Task HandleParentTransitioned__When_ParentCompletedAndAlreadyTerminal__Then_LeaveAlone
            (PlanItemState terminalState)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: terminalState);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        PlanItemTransition.Complete,
                        PlanItemState.Active,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentCompleted>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);

            mockMachine.Object.State.Should().Be(terminalState,
                "Table 8.9's `complete` row explicitly permits an already-terminal child to coexist with a Completed parent");
            testStore.PlanItemState.Should().Be(terminalState);
        }

        // #216 phase 1 (characterization) - this file's ParentSuspended tests assert
        // mockMachine.Object.ParentSuspendState, which is PlanItemStateMachine's OWN copy, set by
        // its Suspended-state OnEntryFrom(ParentSuspend) hook. That copy is set by the state
        // machine regardless of whether the handler raised ParentSuspended before or after firing
        // the transition, so it cannot witness raise ORDER at all.
        //
        // PlanItemStore.Apply(ParentSuspended) sets the OTHER copy - the durable one that survives
        // deactivation and seeds PlanItemStateMachine's ParentSuspendState on rehydration (see its
        // constructor) - and it records whatever PlanItemState is at the moment the event is
        // applied. Raising BEFORE firing therefore records the PRE-suspension state; raising after
        // would record Suspended (HandleTransitioned's own Transitioned raise having already
        // landed), silently breaking resume-after-reactivation while passing every existing test.
        //
        // Wiring RaiseEvent through the store (the pattern established in
        // StageBehaviorTests_HandleSentrySatisfied.cs) is what makes that order observable here.
        [Theory]
        [InlineData(PlanItemTransition.Suspend)]
        [InlineData(PlanItemTransition.ParentSuspend)]
        public async Task HandleParentTransitioned__When_ParentSuspended__Then_StoreRecordsPreSuspensionState
            (PlanItemTransition parentTransition)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage();

            // Active, not the TestPlanItemStore default of Available, so the recorded
            // pre-suspension state is distinguishable from the store's own starting value.
            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleParentTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "CPM",
                        parentInstanceId,
                        "ParentStage",
                        parentTransition,
                        PlanItemState.Active,
                        PlanItemState.Suspended),
                    (StreamSequenceToken)null
                });

            testStore.PlanItemState.Should().Be(PlanItemState.Suspended);

            testStore.ParentSuspendState.Should().Be(PlanItemState.Active,
                "ParentSuspended must be raised BEFORE the ParentSuspend transition fires, or the durable record captures Suspended and a later reactivation resumes to the wrong state");

            // The state machine's own copy agrees - asserted alongside the store's, not instead of
            // it, so a future refactor cannot satisfy one and silently break the other.
            mockMachine.Object.ParentSuspendState.Should().Be(PlanItemState.Active);
        }
    }
}
