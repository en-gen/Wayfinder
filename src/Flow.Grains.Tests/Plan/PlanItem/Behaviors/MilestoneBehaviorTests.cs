using System;
using System.Reflection;
using System.Threading.Tasks;
using Flow.Grains.Events;
using Flow.Grains.Executables;
using Flow.Grains.Expressions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
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
    public class MilestoneBehaviorTests
    {
        [Fact]
        public void Ctor__Given_HostDefMachine__Then_ConfigureMachine()
        {
            var mockHost = new Mock<IBehaviorHost>();
            var milestone = new Milestone();
            var testStore = new TestPlanItemStore(piDef: milestone);
            var mockMachine = new MockPlanItemStateMachine(testStore);

            new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            mockMachine.Verify(x => x.Configure(PlanItemState.Available), Times.Once);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_OutOfScope__Then_Disregard()
        {
            var milestone = new Milestone();

            var testStore = new TestPlanItemStore(piDef: milestone);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.Scope)
                .Returns("scope");

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            await (Task)typeof(MilestoneBehavior)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        "out_of_scope",
                        ShortGuid.NewGuid(),
                        true),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<EntryCriterionSatisfied>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Occur), Times.Never);
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Never);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_NoMatchingCriteria__Then_Disregard()
        {
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem
            {
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        SentryRef = sentryDefinitionId
                    }
                }
            };

            var milestone = new Milestone();

            var testStore = new TestPlanItemStore(piDef: milestone);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            await (Task)typeof(MilestoneBehavior)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        "not_a_match",
                        true),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<EntryCriterionSatisfied>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Occur), Times.Never);
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Never);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_PreviouslyRepeated__Then_RaiseEventDoNotOccur()
        {
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem
            {
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        SentryRef = sentryDefinitionId
                    }
                }
            };

            var milestone = new Milestone();

            var testStore = new TestPlanItemStore(piDef: milestone);
            testStore.Apply(new Repeated());

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            await (Task)typeof(MilestoneBehavior)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        true),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<EntryCriterionSatisfied>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Occur), Times.Never);
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Never);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_EntryCriterionWithOnPart__Then_RaiseEventAndOccur()
        {
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var milestone = new Milestone();

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = milestone.Id,
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        SentryRef = sentryDefinitionId
                    }
                }
            };

            var testStore = new TestPlanItemStore(piDef: milestone, def: pi);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            await (Task)typeof(MilestoneBehavior)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        true),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<EntryCriterionSatisfied>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Occur), Times.Once);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_Repeated__Then_RaiseEventButDoNotRepeat()
        {
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var milestone = new Milestone();

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = milestone.Id,
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        SentryRef = sentryDefinitionId
                    }
                }
            };

            var testStore = new TestPlanItemStore(piDef: milestone, def: pi, initialState: PlanItemState.Completed);
            testStore.Apply(new Repeated());

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            await (Task)typeof(MilestoneBehavior)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        false),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<EntryCriterionSatisfied>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Never);
            mockMachine.Verify(x => x.CanFire(PlanItemTransition.Occur), Times.Never);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Occur), Times.Never);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_TerminalWithoutOnPart__Then_RaiseEventButDoNotRepeat()
        {
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var milestone = new Milestone();

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = milestone.Id,
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        SentryRef = sentryDefinitionId
                    }
                }
            };

            var testStore = new TestPlanItemStore(piDef: milestone, def: pi, initialState: PlanItemState.Completed);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            await (Task)typeof(MilestoneBehavior)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        false),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<EntryCriterionSatisfied>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Occur), Times.Never);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_TerminalWithOnPartAndRepeatedButRepetitionRuleFalse__Then_RaiseEventDoNotRepeat()
        {
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var milestone = new Milestone();

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = milestone.Id,
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        SentryRef = sentryDefinitionId
                    }
                }
            };

            var testStore = new TestPlanItemStore(piDef: milestone, def: pi, initialState: PlanItemState.Completed);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            RepetitionRuleEvaluated capturedRule = null;
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()))
                .Callback<RepetitionRuleEvaluated>(x => capturedRule = x);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            await (Task)typeof(MilestoneBehavior)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        true),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<EntryCriterionSatisfied>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Never);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()), Times.Once);
            capturedRule.Should().NotBeNull();
            capturedRule.Result.Should().BeFalse();

            mockMachine.Verify(x => x.CanFire(PlanItemTransition.Occur), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Occur), Times.Never);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_TerminalWithOnPartAndRepeatedButRepetitionRuleTrue__Then_RaiseEventAndRepeat()
        {
            var caseInstanceId = Guid.NewGuid();
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var milestone = new Milestone();

            var repetitionRule = Rules.IsRepeatableRule;

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = milestone.Id,
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = repetitionRule
                },
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        SentryRef = sentryDefinitionId
                    }
                }
            };

            var testStore = new TestPlanItemStore(piDef: milestone, def: pi, initialState: PlanItemState.Completed);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain.Setup(x => x.ExecuteAsBool(repetitionRule.ContextRef, repetitionRule.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Success(true)));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            RepetitionRuleEvaluated capturedRule = null;
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()))
                .Callback<RepetitionRuleEvaluated>(x => capturedRule = x);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            await (Task)typeof(MilestoneBehavior)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        true),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<EntryCriterionSatisfied>()), Times.Once);

            mockMachine.Verify(x => x.CanFire(PlanItemTransition.Occur), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Occur), Times.Never);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()), Times.Once);
            capturedRule.Should().NotBeNull();
            capturedRule.Result.Should().BeTrue();

            mockHost.Verify(x => x.UnsubscribeFrom<SentrySatisfiedEvent>(sentryDefinitionId));
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()));
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Once);
        }

        [Fact]
        public async Task HandleParentTransitioned__When_ParentInstanceIdEventSource__Then_Disregard()
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var milestone = new Milestone();

            var testStore = new TestPlanItemStore(piDef: milestone);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            await (Task)typeof(MilestoneBehavior)
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

            var milestone = new Milestone();

            var testStore = new TestPlanItemStore(piDef: milestone);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            await (Task)typeof(MilestoneBehavior)
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

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentSuspended>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Suspend), Times.Once);
        }

        [Theory]
        [InlineData(PlanItemTransition.Resume)]
        [InlineData(PlanItemTransition.ParentResume)]
        public async Task HandleParentTransitioned__When_ParentResumed__Then_ResumeToPreviousState
            (PlanItemTransition parentTransition)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var milestone = new Milestone();

            var testStore = new TestPlanItemStore(piDef: milestone);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            // we must ParentSuspend before we can ParentResume
            await (Task)typeof(MilestoneBehavior)
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
            mockMachine.Object.ParentSuspendState.Should().BeNull();

            await (Task)typeof(MilestoneBehavior)
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

            mockMachine.Object.State.Should().Be(PlanItemState.Available);
            mockMachine.Object.ParentSuspendState.Should().BeNull();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentResumed>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Resume), Times.Once);
        }

        [Theory]
        [InlineData(PlanItemTransition.Exit)]
        [InlineData(PlanItemTransition.Terminate)]
        public async Task HandleParentTransitioned__When_ParentTerminated__Then_Exit
            (PlanItemTransition parentTransition)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var milestone = new Milestone();

            var testStore = new TestPlanItemStore(piDef: milestone, def: pi);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestone, mockMachine.Object);

            // we must ParentSuspend before we can ParentResume
            await (Task)typeof(MilestoneBehavior)
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
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.ParentTerminate), Times.Once);
        }
    }
}
