using System;
using System.Reflection;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.PlanItem;
using Wayfinder.Grains.Plan;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Moq;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Plan.PlanItem.Behaviors
{
    // ADO #67 - RepetitionGuardOptions.MaxRepetitionsPerPlanItem enforcement, exercised directly
    // against HandleChildRepeated: the single choke point every repetition path (the no-entry-
    // criteria Complete/Terminate re-evaluation in BaseBehavior.HandleTransitioned/
    // EvaluateRepetitionOnTerminalTransition,
    // AND the entry-criterion OnPart re-satisfaction path in HandleSentrySatisfied) funnels
    // through - via PlanItemRepetitionCriteriaMetEvent - before a repeated child is ever spawned.
    // See RepetitionGuardFootgunIntegrationTests for the full end-to-end #19 foot-gun proof
    // through real grains/streams; this file pins down the boundary logic fast and
    // deterministically, mirroring StageBehaviorTests_HandleChildTransitioned's
    // HandleChildRepeated__When_ChildFound__Then_CreateRepeatInstance harness.
    public partial class StageBehaviorTests
    {
        [Fact]
        public async Task HandleChildRepeated__When_NextRepetitionAtCeiling__Then_RefuseSpawnRaiseCeilingEventAndFaultHost()
        {
            const int ceiling = 3;
            const int currentRepetition = 2; // nextRepetition = 3 == ceiling - must be refused

            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };

            var stage = new Stage
            {
                PlanItems = { pi }
            };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Active);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Address)
                .Returns(address);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object, ceiling);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildRepeated", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemRepetitionCriteriaMetEvent(
                        address,
                        ShortGuid.NewGuid(),
                        planItemDefinitionId,
                        currentRepetition),
                    (StreamSequenceToken) null
                });

            // the loud, observable domain event - carries the repeating definition id, the
            // attempted (refused) repetition index, and the ceiling that refused it
            mockHost.Verify(x => x.RaiseEvent(It.Is<RepetitionCeilingExceeded>(e =>
                e.RepeatingPlanItemDefinitionId == planItemDefinitionId &&
                e.AttemptedRepetition == ceiling &&
                e.Ceiling == ceiling)), Times.Once);

            // no spawn: neither the ChildRepeated bookkeeping event nor an actual child grain call
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);
            mockHost.Verify(x => x.SubscribeTo(
                    It.IsAny<string>(),
                    It.IsAny<Func<PlanItemTransitionedEvent, StreamSequenceToken, Task>>(),
                    It.IsAny<StreamFlags>()),
                Times.Never);

            // the container - not the (already-terminal-or-still-running) repeating child - is
            // the one driven to Fault
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Fault), Times.Once);
            testStore.PlanItemState.Should().Be(PlanItemState.Failed,
                "ADO #67: breaching the ceiling must drive the container to Fault instead of spawning past it");
        }

        [Fact]
        public async Task HandleChildRepeated__When_NextRepetitionOneBelowCeiling__Then_CreateRepeatInstance()
        {
            const int ceiling = 3;
            const int currentRepetition = 1; // nextRepetition = 2 < ceiling - must still be allowed

            var caseInstanceId = Guid.NewGuid();
            var parentInstanceId = ShortGuid.NewGuid();
            var instanceId = ShortGuid.NewGuid();
            var scope = $"CPM.{parentInstanceId}";
            var address = $"{scope}.{instanceId}";
            var planItemDefinitionId = ShortGuid.NewGuid();
            var definitionScope = $"CPM.{ShortGuid.NewGuid()}";

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };

            var stage = new Stage
            {
                PlanItems = { pi }
            };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Active);

            var mockPlanItemGrain = new Mock<IPlanItemInternalGrain>();

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, It.IsAny<string>(), null))
                .Returns(mockPlanItemGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.Address)
                .Returns(address);
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.DefinitionId)
                .Returns(stage.Id);
            mockHost.Setup(x => x.DefinitionScope)
                .Returns(definitionScope);
            mockHost.Setup(x => x.InstanceId)
                .Returns(instanceId);
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object, ceiling);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildRepeated", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemRepetitionCriteriaMetEvent(
                        address,
                        ShortGuid.NewGuid(),
                        planItemDefinitionId,
                        currentRepetition),
                    (StreamSequenceToken) null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionCeilingExceeded>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Fault), Times.Never);

            mockPlanItemGrain.Verify(x => x.DefineRepetition(testStore.CaseDefinitionId, pi, currentRepetition + 1, stage.Id, definitionScope), Times.Once);
            mockPlanItemGrain.Verify(x => x.Trigger(PlanItemTransition.Create), Times.Once);
        }

        // #161 - the redelivery guard's read side, exercised through the real handler: delivering
        // the IDENTICAL PlanItemRepetitionCriteriaMetEvent (same source PlanItemInstanceId) twice
        // must create exactly one child, not two.
        [Fact]
        public async Task HandleChildRepeated__Given_RedeliveredSourceInstanceId__Then_NoChildCreated()
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
            var sourceInstanceId = ShortGuid.NewGuid(); // the repeating child's own instance id

            var pi = new Interfaces.Model.PlanItem { Id = planItemDefinitionId };
            var stage = new Stage { PlanItems = { pi } };

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Active);

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
            // Wire raised events into the real store so the guard recorded by the FIRST delivery
            // is actually visible to the SECOND - this is the read/write round-trip under test.
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object, ceiling);

            var handleChildRepeated = typeof(StageBehavior)
                .GetMethod("HandleChildRepeated", BindingFlags.NonPublic | BindingFlags.Instance);

            var repetitionEvent = new PlanItemRepetitionCriteriaMetEvent(
                address, sourceInstanceId, planItemDefinitionId, currentRepetition);

            // first delivery: genuine, must spawn the child
            await (Task)handleChildRepeated.Invoke(subject, new object[] { repetitionEvent, (StreamSequenceToken)null });

            // second delivery: IDENTICAL event (Orleans at-least-once redelivery, simulated) -
            // must be a no-op
            await (Task)handleChildRepeated.Invoke(subject, new object[] { repetitionEvent, (StreamSequenceToken)null });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Once,
                "a redelivered event must not record the guard a second time");
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Once,
                "#161: a redelivered PlanItemRepetitionCriteriaMetEvent must not spawn a second physical child");
            mockPlanItemGrain.Verify(x => x.DefineRepetition(It.IsAny<string>(), It.IsAny<Interfaces.Model.PlanItem>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            mockPlanItemGrain.Verify(x => x.Trigger(PlanItemTransition.Create), Times.Once);
        }

        // Ordering regression for the #160 fix itself: ConfirmEvents() must run AFTER both raises
        // this handler makes (ChildCreated, raised inside CreateChild, then ChildRepeated,
        // raised after CreateChild returns - see the ordering remarks in HandleChildRepeated for
        // why ChildRepeated is deliberately raised LAST), not merely at some point in the call.
        // Mirrors TimerEventListenerBehaviorTests' call-order recorder (Bug #61/#79 discipline).
        [Fact]
        public async Task HandleChildRepeated__Given_ValidRepetition__Then_ConfirmEventsCalledAfterChildCreatedAndChildRepeatedRaises()
        {
            const int ceiling = 10;
            const int currentRepetition = 0;

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

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Active);

            var mockPlanItemGrain = new Mock<IPlanItemInternalGrain>();

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, It.IsAny<string>(), null))
                .Returns(mockPlanItemGrain.Object);

            var callLog = new System.Collections.Generic.List<string>();

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
                .Callback<object>(x =>
                {
                    callLog.Add($"Raise:{x.GetType().Name}");
                    testStore.Apply((dynamic)x);
                });
            mockHost.Setup(x => x.ConfirmEvents())
                .Callback(() => callLog.Add("Confirm"))
                .Returns(Task.CompletedTask);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object, ceiling);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildRepeated", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemRepetitionCriteriaMetEvent(
                        address, sourceInstanceId, planItemDefinitionId, currentRepetition),
                    (StreamSequenceToken) null
                });

            var childCreatedIndex = callLog.IndexOf($"Raise:{nameof(ChildCreated)}");
            var childRepeatedIndex = callLog.IndexOf($"Raise:{nameof(ChildRepeated)}");
            var confirmIndex = callLog.IndexOf("Confirm");

            childCreatedIndex.Should().BeGreaterOrEqualTo(0);
            childRepeatedIndex.Should().BeGreaterOrEqualTo(0);
            confirmIndex.Should().BeGreaterOrEqualTo(0);

            confirmIndex.Should().BeGreaterThan(childCreatedIndex,
                "ConfirmEvents must run after ChildCreated is raised, or a deactivation between the raise and confirm loses it (#160)");
            confirmIndex.Should().BeGreaterThan(childRepeatedIndex,
                "ConfirmEvents must run after ChildRepeated (the redelivery guard) is raised, or the guard itself is not durable (#160/#161 interaction)");
        }

        // #160 blocker regression: the redelivery guard is recorded AFTER CreateChild succeeds,
        // not before - so a transient failure partway through CreateChild (this test: the child
        // grain's own Trigger(Create) throws once) must NOT poison the guard. Had the guard been
        // recorded before CreateChild ran, this exact retry - the stream agent's own
        // retry-then-drop redelivering the same event after the first attempt's exception - would
        // be silently swallowed as a false-positive "redelivery" and the child would never be
        // created; worse on the timer/no-entry-criteria paths, whose source has already
        // unsubscribed or gone terminal and can never re-request.
        [Fact]
        public async Task HandleChildRepeated__Given_CreateChildThrowsOnFirstAttempt__When_RedeliveredAfterFailure__Then_ChildCreatedOnRetry()
        {
            const int ceiling = 10;
            const int currentRepetition = 0;

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

            var testStore = new TestPlanItemStore(piDef: stage, initialState: PlanItemState.Active);

            var mockPlanItemGrain = new Mock<IPlanItemInternalGrain>();
            mockPlanItemGrain.SetupSequence(x => x.Trigger(PlanItemTransition.Create))
                .ThrowsAsync(new InvalidOperationException("transient failure"))
                .ReturnsAsync((PlanItemSnapshot)null);

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

            var handleChildRepeated = typeof(StageBehavior)
                .GetMethod("HandleChildRepeated", BindingFlags.NonPublic | BindingFlags.Instance);

            var repetitionEvent = new PlanItemRepetitionCriteriaMetEvent(
                address, sourceInstanceId, planItemDefinitionId, currentRepetition);

            Func<Task> firstAttempt = () => (Task)handleChildRepeated.Invoke(
                subject, new object[] { repetitionEvent, (StreamSequenceToken)null });

            await firstAttempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("transient failure");

            // no guard recorded yet - CreateChild never got as far as raising ChildCreated, and
            // ChildRepeated (which now carries the guard) is only raised AFTER CreateChild returns
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Never);

            // the redelivery/retry - must actually create the child this time, not be swallowed
            await (Task)handleChildRepeated.Invoke(subject, new object[] { repetitionEvent, (StreamSequenceToken)null });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Once,
                "the retry after the transient CreateChild failure must create the child - the " +
                "guard must not have been poisoned by the first, failed attempt");
            mockPlanItemGrain.Verify(x => x.Trigger(PlanItemTransition.Create), Times.Exactly(2),
                "first attempt (thrown) then the successful retry");
        }
    }
}
