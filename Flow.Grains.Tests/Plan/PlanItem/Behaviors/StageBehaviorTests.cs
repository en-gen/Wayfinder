using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Plan.PlanningTable;
using Flow.Grains.Plan.Sentry;
using Flow.Grains.Tests.Utils.Helpers;
using Moq;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.Behaviors
{
    public partial class StageBehaviorTests
    {
        [Fact]
        public void Ctor__Given_HostDefMachine__Then_ConfigureMachine()
        {
            var mockHost = new Mock<IBehaviorHost>();
            var stage = new Stage();
            var testStore = new TestPlanItemStore(piDef: stage);
            var mockMachine = new MockPlanItemStateMachine(testStore);

            new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            mockMachine.Verify(x => x.Configure(PlanItemState.Available), Times.Once);
            mockMachine.Verify(x => x.Configure(PlanItemState.Active), Times.Once);
        }

        [Fact]
        public async Task Activate__Given_StageWithPlanItems__When_Defined__Then_DefineSentriesResumeSubscriptions()
        {
            var caseInstanceId = ShortGuid.NewGuid();
            var parentInstanceId = ShortGuid.NewGuid();
            var instanceId = ShortGuid.NewGuid();
            var entrySentryId = ShortGuid.NewGuid();
            var exitSentryId = ShortGuid.NewGuid();
            var scope = ShortGuid.NewGuid();

            var planItem = new Interfaces.Model.PlanItem
            {
                EntryCriteria = { new EntryCriterion { SentryRef = entrySentryId } },
                ExitCriteria = { new ExitCriterion { SentryRef = exitSentryId } }
            };

            var stage = new Stage
            {
                Sentries =
                {
                    new Interfaces.Model.Sentry(),
                    new Interfaces.Model.Sentry()
                },
                PlanItems =
                {
                    new Interfaces.Model.PlanItem(),
                    new Interfaces.Model.PlanItem(),
                    new Interfaces.Model.PlanItem()
                },
                PlanningTable = new Interfaces.Model.PlanningTable()
            };

            var testStore = new TestPlanItemStore();

            var mockSentryGrain = new Mock<ISentryGrain>();
            mockSentryGrain
                .Setup(x => x.Defined())
                .Returns(Task.FromResult(false));
            mockSentryGrain
                .Setup(x => x.Define(testStore.CaseDefinitionId, It.IsAny<Interfaces.Model.Sentry>()))
                .Returns(Task.CompletedTask);

            var mockPlanningTableGrain = new Mock<IPlanningTableGrain>();
            mockPlanningTableGrain
                .Setup(x => x.Defined())
                .Returns(Task.FromResult(false));
            mockPlanningTableGrain
                .Setup(x => x.Define(caseInstanceId, stage.PlanningTable))
                .Returns(Task.CompletedTask);

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<ISentryGrain>(caseInstanceId, It.IsAny<string>(), null))
                .Returns(mockSentryGrain.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanningTableGrain>(caseInstanceId, instanceId, null))
                .Returns(mockPlanningTableGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.InstanceId)
                .Returns(instanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(planItem);
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await subject.Activate();

            // subscriptions to children transitions and repetitions
            foreach (var pi in stage.PlanItems)
            {
                mockHost.Verify(x => x.SubscribeTo(
                    pi.Id,
                    It.IsAny<Func<PlanItemTransitionedEvent, StreamSequenceToken, Task>>(),
                    StreamFlags.Resume),
                    Times.Once);
                mockHost.Verify(x => x.SubscribeTo(
                    pi.Id,
                    It.IsAny<Func<PlanItemRepetitionCriteriaMetEvent, StreamSequenceToken, Task>>(),
                    StreamFlags.Resume),
                    Times.Once);
            }

            // subscription to parent transitions (base behavior)
            mockHost.Verify(x => x.SubscribeTo(
                parentInstanceId,
                It.IsAny<Func<PlanItemTransitionedEvent, StreamSequenceToken, Task>>(),
                StreamFlags.Create | StreamFlags.Resume),
                Times.Once());

            // resume criteria subscriptions (base behavior)
            mockHost.Verify(x => x.SubscribeTo(
                entrySentryId,
                It.IsAny<Func<SentrySatisfiedEvent, StreamSequenceToken, Task>>(),
                StreamFlags.Resume),
                Times.Once);

            mockHost.Verify(x => x.SubscribeTo(
                exitSentryId,
                It.IsAny<Func<SentrySatisfiedEvent, StreamSequenceToken, Task>>(),
                StreamFlags.Resume),
                Times.Once);

            // create entry criteria subscriptions
            mockHost.Verify(x => x.SubscribeTo(
                entrySentryId,
                It.IsAny<Func<SentrySatisfiedEvent, StreamSequenceToken, Task>>(),
                StreamFlags.Create | StreamFlags.Resume),
                Times.Once);

            // define sentries
            mockSentryGrain.Verify(x => x.Defined(), Times.Exactly(2));
            foreach (var sentry in stage.Sentries)
            {
                mockSentryGrain.Verify(x => x.Define(testStore.CaseDefinitionId, sentry), Times.Once);
            }

            mockPlanningTableGrain.Verify(x => x.Defined(), Times.Once);
            mockPlanningTableGrain.Verify(x => x.Define(testStore.CaseDefinitionId, stage.PlanningTable), Times.Once);
        }

        [Fact]
        public async Task HandleEnterActiveFromStart__Given_StageWithPlanItems__Then_CreateEachChild()
        {
            var caseInstanceId = Guid.NewGuid();
            var parentInstanceId = ShortGuid.NewGuid();
            var instanceId = ShortGuid.NewGuid();
            var scope = ShortGuid.NewGuid();

            var stage = new Stage
            {
                PlanItems =
                {
                    new Interfaces.Model.PlanItem(),
                    new Interfaces.Model.PlanItem()
                }
            };

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockPlanItemGrain = new Mock<IPlanItemGrain>();

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemGrain>(caseInstanceId, It.IsAny<string>(), null))
                .Returns(mockPlanItemGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.Address)
                .Returns($"CPM.{parentInstanceId}");
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.InstanceId)
                .Returns(instanceId);
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            new StageBehavior(
                mockHost.Object,
                stage,
                mockMachine.Object);

            // transition to start to trigger creating children
            await mockMachine.Object.FireAsync(PlanItemTransition.Start);

            foreach (var pi in stage.PlanItems)
            {
                mockPlanItemGrain.Verify(x => x.DefineRepetition(testStore.CaseDefinitionId, pi, 0), Times.Once);

                mockHost.Verify(x => x.SubscribeTo(
                        pi.Id,
                        It.IsAny<Func<PlanItemTransitionedEvent, StreamSequenceToken, Task>>(),
                        StreamFlags.Create),
                    Times.Once);
                mockHost.Verify(x => x.SubscribeTo(
                        pi.Id,
                        It.IsAny<Func<PlanItemRepetitionCriteriaMetEvent, StreamSequenceToken, Task>>(),
                        StreamFlags.Create),
                    Times.Once);
            }

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Exactly(2));
            mockPlanItemGrain.Verify(x => x.Trigger(PlanItemTransition.Create), Times.Exactly(2));
        }
    }
}
