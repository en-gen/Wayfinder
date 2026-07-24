using System;
using System.Reflection;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Executables;
using Flow.Grains.Expressions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Plan.PlanningTable;
using Flow.Grains.Plan.Sentry;
using Flow.Grains.Tests.Utils.Helpers;
using FluentAssertions;
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

        // Pinning test for the Task.Factory.StartNew(async () => ...) anti-pattern that used to wrap
        // this branch: StartNew with an async lambda returns Task<Task>, so the inner task (and any
        // exception it throws) was never awaited/observed by the Task.WhenAll in
        // HandleEnterAvailableFromCreate. Now that the branch is a directly-awaited call, an exception
        // thrown while evaluating the ManualActivationRule must propagate out of
        // HandleEnterAvailableFromCreate instead of vanishing.
        [Fact]
        public async Task HandleEnterAvailableFromCreate__When_NoEntryCriteriaAndManualActivationRuleThrows__Then_ExceptionSurfaces()
        {
            var caseInstanceId = Guid.NewGuid();

            var stage = new Stage();

            var manualActivationRule = Rules.IsManuallyActivated;

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = stage.Id,
                ItemControl = new PlanItemControl
                {
                    ManualActivationRule = manualActivationRule
                }
            };

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Uninitialized);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsBool(manualActivationRule.ContextRef, manualActivationRule.Condition))
                .ThrowsAsync(new InvalidOperationException("boom"));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            Func<Task> act = () => (Task)typeof(StageBehavior)
                .GetMethod("HandleEnterAvailableFromCreate", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[0]);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        }

        // Pinning test for the Task.Factory.StartNew(async () => ...) anti-pattern that used to wrap
        // the planning table definition inside Define(): the exception thrown by the planning table
        // grain must now surface through Define()/Task.WhenAll rather than being silently dropped.
        [Fact]
        public async Task DefinePlanningTable__When_PlanningTableGrainThrows__Then_ExceptionSurfaces()
        {
            var caseInstanceId = Guid.NewGuid();
            var instanceId = ShortGuid.NewGuid();

            var stage = new Stage
            {
                PlanningTable = new Interfaces.Model.PlanningTable()
            };

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockPlanningTableGrain = new Mock<IPlanningTableGrain>();
            mockPlanningTableGrain
                .Setup(x => x.Defined())
                .ThrowsAsync(new InvalidOperationException("boom"));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanningTableGrain>(caseInstanceId, instanceId, null))
                .Returns(mockPlanningTableGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.InstanceId)
                .Returns(instanceId);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            Func<Task> act = () => (Task)typeof(StageBehavior)
                .GetMethod("DefinePlanningTable", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[0]);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
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

            var parentDefinitionId = ShortGuid.NewGuid();

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.ParentDefinitionId)
                .Returns(parentDefinitionId);
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

            // subscription to parent transitions (base behavior) - keyed on the parent's
            // DEFINITION id, not its instance id (#63): publish is always on Definition.Id, so
            // the subscribe side must match that key or the cascade never arrives.
            mockHost.Verify(x => x.SubscribeTo(
                parentDefinitionId,
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

        // D6 - a Stage's ExitCriteria must be subscribed on the create path, not left to
        // base.Activate()'s Resume-only subscription (which is a no-op unless a subscription handle
        // already exists - see StageBehavior.HandleEnterAvailableFromCreate's remarks). Mirrors
        // TaskBehavior's existing (IsBlocking-gated) exit-criteria-on-create subscription, but
        // unconditional per Table 5.34 (no equivalent gate exists for Stage.exitCriteria).
        [Fact]
        public async Task HandleEnterAvailableFromCreate__Given_StageWithExitCriteria__Then_SubscribeExitCriteriaWithCreateFlag()
        {
            var exitSentryId = ShortGuid.NewGuid();

            var stage = new Stage();

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = stage.Id,
                ExitCriteria = { new ExitCriterion { SentryRef = exitSentryId } }
            };

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Uninitialized);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleEnterAvailableFromCreate", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[0]);

            mockHost.Verify(x => x.SubscribeTo(
                exitSentryId,
                It.IsAny<Func<SentrySatisfiedEvent, StreamSequenceToken, Task>>(),
                StreamFlags.Create),
                Times.Once);
        }

        [Fact]
        public async Task HandleEnterActiveFromStart__Given_StageWithPlanItems__Then_CreateEachChild()
        {
            var caseInstanceId = Guid.NewGuid();
            var parentInstanceId = ShortGuid.NewGuid();
            var instanceId = ShortGuid.NewGuid();
            var scope = ShortGuid.NewGuid();
            var definitionScope = $"CPM.{ShortGuid.NewGuid()}";

            var stage = new Stage
            {
                PlanItems =
                {
                    new Interfaces.Model.PlanItem(),
                    new Interfaces.Model.PlanItem()
                }
            };

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockPlanItemGrain = new Mock<IPlanItemInternalGrain>();

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, It.IsAny<string>(), null))
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
            mockHost.Setup(x => x.DefinitionId)
                .Returns(stage.Id);
            mockHost.Setup(x => x.DefinitionScope)
                .Returns(definitionScope);
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
                // parentDefinitionId (#63): CreateChild threads Host.DefinitionId (this Stage's
                // own definition id) through so the child keys its parent-transition subscription
                // on the same stream this Stage publishes its transitions on.
                // parentDefinitionScope (#65): CreateChild also threads Host.DefinitionScope (this
                // Stage's own full definition-scope path) through so the child's definition-index
                // lookup searches from the correct definition-tree position.
                mockPlanItemGrain.Verify(x => x.DefineRepetition(testStore.CaseDefinitionId, pi, 0, stage.Id, definitionScope), Times.Once);

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
