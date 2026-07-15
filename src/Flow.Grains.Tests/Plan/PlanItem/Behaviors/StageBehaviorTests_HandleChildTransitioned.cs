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
using Microsoft.Extensions.Logging;
using Moq;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.Behaviors
{
    public partial class StageBehaviorTests
    {
        [Fact]
        public async Task HandleChildRepeated__When_OutOfScope__Then_Disregard()
        {
            var address = ShortGuid.NewGuid();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Address)
                .Returns(address);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildRepeated", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemRepetitionCriteriaMetEvent(
                        "not_a_match",
                        "not_a_match",
                        "not_a_match",
                        0),
                    (StreamSequenceToken) null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never);
        }

        [Fact]
        public async Task HandleChildRepeated__When_ChildNotFound__Then_Disregard()
        {
            var address = ShortGuid.NewGuid();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Address)
                .Returns(address);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildRepeated", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemRepetitionCriteriaMetEvent(
                        address,
                        "not_a_match",
                        "not_a_match",
                        0),
                    (StreamSequenceToken) null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Never);
        }

        [Fact]
        public async Task HandleChildRepeated__When_ChildFound__Then_CreateRepeatInstance()
        {
            var caseInstanceId = Guid.NewGuid();
            var parentInstanceId = ShortGuid.NewGuid();
            var instanceId = ShortGuid.NewGuid();
            var scope = $"CPM.{parentInstanceId}";
            var address = $"{scope}.{instanceId}";
            var planItemDefinitionId = ShortGuid.NewGuid();
            var definitionScope = $"CPM.{ShortGuid.NewGuid()}";

            var pi = new Interfaces.Model.PlanItem{ Id = planItemDefinitionId};

            var stage = new Stage
            {
                PlanItems =
                {
                    pi
                }
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

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);
            
            await (Task) typeof(StageBehavior)
                .GetMethod("HandleChildRepeated", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemRepetitionCriteriaMetEvent(
                        address,
                        ShortGuid.NewGuid(),
                        planItemDefinitionId,
                        0),
                    (StreamSequenceToken) null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildRepeated>()), Times.Once);

            // parentDefinitionId (#63): threaded from Host.DefinitionId (this Stage's own
            // definition id) so the repeated child's parent-transition subscription resolves.
            // parentDefinitionScope (#65): threaded from Host.DefinitionScope (this Stage's own
            // full definition-scope path) so the repeated child's definition-index lookup resolves.
            mockPlanItemGrain.Verify(x => x.DefineRepetition(testStore.CaseDefinitionId, pi, 1, stage.Id, definitionScope), Times.Once);
            mockPlanItemGrain.Verify(x => x.Trigger(PlanItemTransition.Create), Times.Once);

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

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildCreated>()), Times.Once);
        }

        [Fact]
        public async Task HandleChildTransitioned__When_OutOfScope__Then_Disregard()
        {
            var address = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage, def: pi);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.Address)
                .Returns(address);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        "not_a_match",
                        ShortGuid.NewGuid(),
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Occur,
                        PlanItemState.Active,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.LogWithContext(It.IsAny<Action<ILogger>>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<object>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);
        }

        [Fact]
        public async Task HandleChildTransitioned__Given_Uninitialized__When_ChildrenNotTerminalManuallyActivated__Then_RaiseChildTriggeredActivationAndFireTransitionsToStart()
        {
            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();
            var planItemInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Uninitialized);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemDefinitionId,
                PlanItemInstanceId = planItemInstanceId,
                Repetition = 0
            });

            var snapshot = new PlanItemSnapshot
            {
                Required = true,
                PlanItemState = PlanItemState.Active
            };

            var mockPlanItem = new Mock<IPlanItemInternalGrain>();
            mockPlanItem.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshot));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemInstanceId}", null))
                .Returns(mockPlanItem.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Address)
                .Returns(address);
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

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        address,
                        ShortGuid.NewGuid(),
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Create,
                        PlanItemState.Uninitialized,
                        PlanItemState.Available),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildTriggeredActivation>()), Times.Once);

            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Create), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.ManualStart), Times.Once);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Never);
        }

        [Fact]
        public async Task HandleChildTransitioned__Given_Uninitialized__When_ChildrenNotTerminalEntryCriteria__Then_RaiseChildTriggeredActivationAndFireTransitionsToStart()
        {
            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();
            var planItemInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem
            {
                EntryCriteria =
                {
                    new EntryCriterion {SentryRef = ShortGuid.NewGuid()}
                }
            };

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Uninitialized);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemDefinitionId,
                PlanItemInstanceId = planItemInstanceId,
                Repetition = 0
            });

            var snapshot = new PlanItemSnapshot
            {
                Required = true,
                PlanItemState = PlanItemState.Active
            };

            var mockPlanItem = new Mock<IPlanItemInternalGrain>();
            mockPlanItem.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshot));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemInstanceId}", null))
                .Returns(mockPlanItem.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Address)
                .Returns(address);
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

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        address,
                        ShortGuid.NewGuid(),
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Create,
                        PlanItemState.Uninitialized,
                        PlanItemState.Available),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildTriggeredActivation>()), Times.Once);

            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Create), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Start), Times.Once);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Never);
        }

        [Fact]
        public async Task HandleChildTransitioned__Given_Disabled__When_ChildrenNotTerminal__Then_RaiseChildTriggeredActivationAndFireTransitionsToStart()
        {
            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();
            var planItemDefinitionId = ShortGuid.NewGuid();
            var planItemInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Disabled);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemDefinitionId,
                PlanItemInstanceId = planItemInstanceId,
                Repetition = 0
            });

            var snapshot = new PlanItemSnapshot
            {
                Required = true,
                PlanItemState = PlanItemState.Active
            };

            var mockPlanItem = new Mock<IPlanItemInternalGrain>();
            mockPlanItem.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshot));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemInstanceId}", null))
                .Returns(mockPlanItem.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Address)
                .Returns(address);
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

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        address,
                        ShortGuid.NewGuid(),
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Create,
                        PlanItemState.Uninitialized,
                        PlanItemState.Available),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ChildTriggeredActivation>()), Times.Once);

            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Reenable), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.ManualStart), Times.Once);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Never);
        }

        [Fact]
        public async Task HandleChildTransitioned__Given_Active__When_NotAutocompleteChildrenNotTerminal__Then_Disregard()
        {
            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();
            var planItemADefinitionId = ShortGuid.NewGuid();
            var planItemAInstanceId = ShortGuid.NewGuid();
            var planItemBDefinitionId = ShortGuid.NewGuid();
            var planItemBInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemADefinitionId,
                PlanItemInstanceId = planItemAInstanceId,
                Repetition = 0
            });
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemBDefinitionId,
                PlanItemInstanceId = planItemBInstanceId,
                Repetition = 0
            });

            var snapshotA = new PlanItemSnapshot
            {
                Required = true,
                PlanItemState = PlanItemState.Completed
            };
            var snapshotB = new PlanItemSnapshot
            {
                Required = true,
                PlanItemState = PlanItemState.Active
            };

            var mockPlanItemA = new Mock<IPlanItemInternalGrain>();
            mockPlanItemA.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotA));
            var mockPlanItemB = new Mock<IPlanItemInternalGrain>();
            mockPlanItemB.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotB));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemAInstanceId}", null))
                .Returns(mockPlanItemA.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemBInstanceId}", null))
                .Returns(mockPlanItemB.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Address)
                .Returns(address);
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

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        address,
                        ShortGuid.NewGuid(),
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Occur,
                        PlanItemState.Available,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });
            
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<object>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);
        }

        [Fact]
        public async Task HandleChildTransitioned__Given_Active__When_AutocompleteChildrenNotTerminal__Then_Disregard()
        {
            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();
            var planItemADefinitionId = ShortGuid.NewGuid();
            var planItemAInstanceId = ShortGuid.NewGuid();
            var planItemBDefinitionId = ShortGuid.NewGuid();
            var planItemBInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage
            {
                AutoComplete = true
            };

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemADefinitionId,
                PlanItemInstanceId = planItemAInstanceId,
                Repetition = 0
            });
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemBDefinitionId,
                PlanItemInstanceId = planItemBInstanceId,
                Repetition = 0
            });

            var snapshotA = new PlanItemSnapshot
            {
                Required = true,
                PlanItemState = PlanItemState.Completed
            };
            var snapshotB = new PlanItemSnapshot
            {
                Required = true,
                PlanItemState = PlanItemState.Active
            };

            var mockPlanItemA = new Mock<IPlanItemInternalGrain>();
            mockPlanItemA.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotA));
            var mockPlanItemB = new Mock<IPlanItemInternalGrain>();
            mockPlanItemB.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotB));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemAInstanceId}", null))
                .Returns(mockPlanItemA.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemBInstanceId}", null))
                .Returns(mockPlanItemB.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Address)
                .Returns(address);
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

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        address,
                        ShortGuid.NewGuid(),
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Occur,
                        PlanItemState.Available,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<object>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);
        }

        [Fact]
        public async Task HandleChildTransitioned__Given_Active__When_AutocompleteChildrenTerminal__Then_Complete()
        {
            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();
            var planItemADefinitionId = ShortGuid.NewGuid();
            var planItemAInstanceId = ShortGuid.NewGuid();
            var planItemBDefinitionId = ShortGuid.NewGuid();
            var planItemBInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();;

            var stage = new Stage
            {
                AutoComplete = true
            };

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemADefinitionId,
                PlanItemInstanceId = planItemAInstanceId,
                Repetition = 0
            });
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemBDefinitionId,
                PlanItemInstanceId = planItemBInstanceId,
                Repetition = 0
            });

            var snapshotA = new PlanItemSnapshot
            {
                Required = true,
                PlanItemState = PlanItemState.Completed
            };
            var snapshotB = new PlanItemSnapshot
            {
                Required = true,
                PlanItemState = PlanItemState.Completed
            };

            var mockPlanItemA = new Mock<IPlanItemInternalGrain>();
            mockPlanItemA.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotA));
            var mockPlanItemB = new Mock<IPlanItemInternalGrain>();
            mockPlanItemB.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotB));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemAInstanceId}", null))
                .Returns(mockPlanItemA.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemBInstanceId}", null))
                .Returns(mockPlanItemB.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Address)
                .Returns(address);
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

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        address,
                        ShortGuid.NewGuid(),
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Occur,
                        PlanItemState.Available,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });

            // #68 (Table 8.12, D4 residual): autoComplete=TRUE has no Manual Completion branch -
            // it auto-completes with no human involvement, so UserCompletable must never latch
            // true for this Stage, even though the AutoComplete column itself is satisfied here.
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<AutoCompleteCriteriaMet>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Once);
        }

        [Fact]
        public async Task HandleChildTransitioned__Given_Active__When_NotAutocompleteChildrenTerminal__Then_Complete()
        {
            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();
            var planItemADefinitionId = ShortGuid.NewGuid();
            var planItemAInstanceId = ShortGuid.NewGuid();
            var planItemBDefinitionId = ShortGuid.NewGuid();
            var planItemBInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem(); ;

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemADefinitionId,
                PlanItemInstanceId = planItemAInstanceId,
                Repetition = 0
            });
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemBDefinitionId,
                PlanItemInstanceId = planItemBInstanceId,
                Repetition = 0
            });

            var snapshotA = new PlanItemSnapshot
            {
                Required = true,
                PlanItemState = PlanItemState.Completed
            };
            var snapshotB = new PlanItemSnapshot
            {
                Required = true,
                PlanItemState = PlanItemState.Completed
            };

            var mockPlanItemA = new Mock<IPlanItemInternalGrain>();
            mockPlanItemA.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotA));
            var mockPlanItemB = new Mock<IPlanItemInternalGrain>();
            mockPlanItemB.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotB));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemAInstanceId}", null))
                .Returns(mockPlanItemA.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemBInstanceId}", null))
                .Returns(mockPlanItemB.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Address)
                .Returns(address);
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

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        address,
                        ShortGuid.NewGuid(),
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Occur,
                        PlanItemState.Available,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<FullyCompleteCriteriaMet>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Once);
        }

        [Fact]
        public async Task HandleChildTransitioned__Given_Active__When_NotAutocompleteAllDiscretionaryPlanned__Then_Complete()
        {
            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();

            var planItemADefinitionId = ShortGuid.NewGuid();
            var planItemAInstanceId = ShortGuid.NewGuid();
            var piA = new Interfaces.Model.PlanItem {Id = planItemADefinitionId};

            var planItemBDefinitionId = ShortGuid.NewGuid();
            var planItemBInstanceId = ShortGuid.NewGuid();
            var piB = new Interfaces.Model.PlanItem { Id = planItemBDefinitionId };

            var discretionaryItemCDefinitionId = ShortGuid.NewGuid();
            var discretionaryItemCInstanceId = ShortGuid.NewGuid();
            var piC = new Interfaces.Model.PlanItem { Id = discretionaryItemCDefinitionId };

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage
            {
                PlanningTable = new Interfaces.Model.PlanningTable
                {
                    TableItems =
                    {
                        new DiscretionaryItem {Id = discretionaryItemCDefinitionId}
                    }
                }
            };

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemADefinitionId,
                PlanItemInstanceId = planItemAInstanceId,
                Repetition = 0
            });
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemBDefinitionId,
                PlanItemInstanceId = planItemBInstanceId,
                Repetition = 0
            });
            testStore.Apply(new ChildCreated
            {
                PlanItemId = discretionaryItemCDefinitionId,
                PlanItemInstanceId = discretionaryItemCInstanceId,
                Repetition = 0
            });

            var snapshotA = new PlanItemSnapshot
            {
                Definition = piA,
                Required = true,
                PlanItemState = PlanItemState.Completed
            };
            var snapshotB = new PlanItemSnapshot
            {
                Definition = piB,
                Required = true,
                PlanItemState = PlanItemState.Completed
            };
            var snapshotC = new PlanItemSnapshot
            {
                Definition = piC,
                Required = true,
                PlanItemState = PlanItemState.Completed
            };

            var mockPlanItemA = new Mock<IPlanItemInternalGrain>();
            mockPlanItemA.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotA));
            var mockPlanItemB = new Mock<IPlanItemInternalGrain>();
            mockPlanItemB.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotB));
            var mockPlanItemC = new Mock<IPlanItemInternalGrain>();
            mockPlanItemC.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotC));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemAInstanceId}", null))
                .Returns(mockPlanItemA.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemBInstanceId}", null))
                .Returns(mockPlanItemB.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{discretionaryItemCInstanceId}", null))
                .Returns(mockPlanItemC.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Address)
                .Returns(address);
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

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        address,
                        ShortGuid.NewGuid(),
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Occur,
                        PlanItemState.Available,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<FullyCompleteCriteriaMet>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Once);
        }

        [Fact]
        public async Task HandleChildTransitioned__Given_Active__When_NonRequiredTaskTerminal__Then_UserCompletable()
        {
            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();

            var planItemADefinitionId = ShortGuid.NewGuid();
            var planItemAInstanceId = ShortGuid.NewGuid();
            var piA = new Interfaces.Model.PlanItem { Id = planItemADefinitionId };

            var planItemBDefinitionId = ShortGuid.NewGuid();
            var planItemBInstanceId = ShortGuid.NewGuid();
            var piB = new Interfaces.Model.PlanItem { Id = planItemBDefinitionId };

            var discretionaryItemCDefinitionId = ShortGuid.NewGuid();
            var discretionaryItemCInstanceId = ShortGuid.NewGuid();
            var piC = new Interfaces.Model.PlanItem { Id = discretionaryItemCDefinitionId };

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage
            {
                PlanningTable = new Interfaces.Model.PlanningTable
                {
                    TableItems =
                    {
                        new DiscretionaryItem {Id = discretionaryItemCDefinitionId}
                    }
                }
            };

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemADefinitionId,
                PlanItemInstanceId = planItemAInstanceId,
                Repetition = 0
            });
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemBDefinitionId,
                PlanItemInstanceId = planItemBInstanceId,
                Repetition = 0
            });
            testStore.Apply(new ChildCreated
            {
                PlanItemId = discretionaryItemCDefinitionId,
                PlanItemInstanceId = discretionaryItemCInstanceId,
                Repetition = 0
            });

            var snapshotA = new PlanItemSnapshot
            {
                Definition = piA,
                Required = true,
                PlanItemState = PlanItemState.Completed
            };
            var snapshotB = new PlanItemSnapshot
            {
                Definition = piB,
                Required = true,
                PlanItemState = PlanItemState.Completed
            };
            var snapshotC = new PlanItemSnapshot
            {
                Definition = piC,
                Required = false,
                PlanItemState = PlanItemState.Enabled
            };

            var mockPlanItemA = new Mock<IPlanItemInternalGrain>();
            mockPlanItemA.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotA));
            var mockPlanItemB = new Mock<IPlanItemInternalGrain>();
            mockPlanItemB.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotB));
            var mockPlanItemC = new Mock<IPlanItemInternalGrain>();
            mockPlanItemC.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotC));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemAInstanceId}", null))
                .Returns(mockPlanItemA.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemBInstanceId}", null))
                .Returns(mockPlanItemB.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{discretionaryItemCInstanceId}", null))
                .Returns(mockPlanItemC.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Address)
                .Returns(address);
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

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        address,
                        ShortGuid.NewGuid(),
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Occur,
                        PlanItemState.Available,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<FullyCompleteCriteriaMet>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Never);
        }

        // #68 (Table 8.12, D4 residual) - the headline scenario the fix targets: autoComplete=
        // FALSE's Manual Completion OR-branch carries no "no Active children" conjunct, so a
        // still-Active NON-required child must not block UserCompletable once every REQUIRED
        // child is terminal. Mirrors the integration-level KnownGapScenarios probe
        // (StageCompletion__Given_AutoCompleteFalseAndNonRequiredChildActive__
        // Then_ManualCompletionBecomesAvailable) at the fast unit-test layer.
        [Fact]
        public async Task HandleChildTransitioned__Given_NotAutocompleteAndRequiredTerminal__When_NonRequiredChildActive__Then_UserCompletable()
        {
            var caseInstanceId = Guid.NewGuid();
            var address = ShortGuid.NewGuid();
            var planItemADefinitionId = ShortGuid.NewGuid();
            var planItemAInstanceId = ShortGuid.NewGuid();
            var planItemBDefinitionId = ShortGuid.NewGuid();
            var planItemBInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemADefinitionId,
                PlanItemInstanceId = planItemAInstanceId,
                Repetition = 0
            });
            testStore.Apply(new ChildCreated
            {
                PlanItemId = planItemBDefinitionId,
                PlanItemInstanceId = planItemBInstanceId,
                Repetition = 0
            });

            // A: the only required child, now terminal.
            var snapshotA = new PlanItemSnapshot
            {
                Required = true,
                PlanItemState = PlanItemState.Completed
            };
            // B: non-required, deliberately still Active - must not block UserCompletable.
            var snapshotB = new PlanItemSnapshot
            {
                Required = false,
                PlanItemState = PlanItemState.Active
            };

            var mockPlanItemA = new Mock<IPlanItemInternalGrain>();
            mockPlanItemA.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotA));
            var mockPlanItemB = new Mock<IPlanItemInternalGrain>();
            mockPlanItemB.Setup(x => x.GetSnapshot())
                .Returns(Task.FromResult(snapshotB));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemAInstanceId}", null))
                .Returns(mockPlanItemA.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{address}.{planItemBInstanceId}", null))
                .Returns(mockPlanItemB.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Address)
                .Returns(address);
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

            // The required child A is the one transitioning to Completed; non-required B stays
            // Active throughout.
            await (Task)typeof(StageBehavior)
                .GetMethod("HandleChildTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new PlanItemTransitionedEvent(
                        address,
                        ShortGuid.NewGuid(),
                        ShortGuid.NewGuid(),
                        PlanItemTransition.Complete,
                        PlanItemState.Active,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<FullyCompleteCriteriaMet>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Complete), Times.Never);
        }
    }
}
