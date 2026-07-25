using System;
using System.Reflection;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Moq;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.Behaviors
{
    // ADO #67 - RepetitionGuardOptions.MaxRepetitionsPerPlanItem enforcement, exercised directly
    // against HandleChildRepeated: the single choke point every repetition path (the no-entry-
    // criteria Complete/Terminate re-evaluation in BaseBehavior.TryRepeatOnCompleteOrTerminate,
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
    }
}
