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
    public partial class TaskBehaviorTests
    {
        [Fact]
        public async Task HandleParentTransitioned__When_ParentInstanceIdEventSource__Then_Disregard()
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var task = new HumanTask();

            var testStore = new TestPlanItemStore(piDef: task);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
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

            var task = new HumanTask();

            var testStore = new TestPlanItemStore(piDef: task);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
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

            var task = new HumanTask();

            var testStore = new TestPlanItemStore(piDef: task);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            // we must ParentSuspend before we can ParentResume
            await (Task)typeof(TaskBehavior<HumanTask>)
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

            await (Task)typeof(TaskBehavior<HumanTask>)
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

            var task = new HumanTask();

            var testStore = new TestPlanItemStore(piDef: task, def: pi);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
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

        // #216 phase 1 (characterization) - the Task half of #179's `complete` cascade, which had
        // no unit coverage at all. Table 8.9's `complete` rows put Task instances in the SAME
        // column as Stage instances: {Available, Enabled, Active, Suspended} carry Transition = N/A
        // and To state = `<impossible>`. So this is deliberately the mirror image of
        // StageBehaviorTests_HandleParentTransitioned's own pair - if the two ever diverge, one of
        // them is wrong.
        //
        // Firing `exit` is #179's RECONCILIATION of that `<impossible>` cell with Table 8.12, not
        // something Table 8.9 prescribes (its N/A means no transition is named; the `exit,
        // terminate` rows are where the table does name one). See
        // StageBehaviorTests_HandleParentTransitioned's twin of this test for the full argument and
        // for what a future author is and is not free to reinterpret. See also
        // TaskBehavior.HandleParentTransitioned's Complete case.
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

            var task = new HumanTask();

            var testStore = new TestPlanItemStore(piDef: task, def: pi, initialState: hostState);

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

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
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
                "Table 8.9 marks a non-terminal Task child of a Completed parent `<impossible>` (Transition = N/A); #179 reconciles that with Table 8.12 by cascading exit, which is this repo's design decision rather than the table's own prescription - see this test's remarks");
        }

        // #216 phase 1 (characterization) - Table 8.9's "may coexist" set, exactly
        // PlanItemState.IsTerminal(). ConfigureForStageOrTask (shared by Stage and Task) permits
        // Exit from Disabled and Failed, so a bare CanFire(Exit) gate would wrongly cascade into
        // both; this pins that the Complete arm carries its own, narrower IsTerminal() condition.
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

            var task = new HumanTask();

            var testStore = new TestPlanItemStore(piDef: task, def: pi, initialState: terminalState);

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

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
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

        // #216 phase 1 (characterization) - see
        // StageBehaviorTests_HandleParentTransitioned's twin of this test for the full rationale:
        // the machine's own ParentSuspendState copy is set by its OnEntryFrom hook and is blind to
        // raise ORDER, while PlanItemStore.Apply(ParentSuspended) records whatever PlanItemState is
        // current when the event lands. Only the store's copy survives deactivation and seeds the
        // machine on rehydration, and until now nothing asserted it on either behavior.
        [Theory]
        [InlineData(PlanItemTransition.Suspend)]
        [InlineData(PlanItemTransition.ParentSuspend)]
        public async Task HandleParentTransitioned__When_ParentSuspended__Then_StoreRecordsPreSuspensionState
            (PlanItemTransition parentTransition)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var task = new HumanTask();

            // Active, not the TestPlanItemStore default of Available, so the recorded
            // pre-suspension state is distinguishable from the store's own starting value.
            var testStore = new TestPlanItemStore(piDef: task, def: pi, initialState: PlanItemState.Active);

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

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
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

            mockMachine.Object.ParentSuspendState.Should().Be(PlanItemState.Active);
        }
    }
}
