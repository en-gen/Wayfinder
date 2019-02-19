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
    public partial class StageBehaviorTests
    {
        [Fact]
        public async Task HandleSentrySatisfied__When_OutOfScope__Then_Disregard()
        {
            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.Scope)
                .Returns("scope");

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
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
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Start), Times.Never);
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

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
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
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Start), Times.Never);
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Never);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_EntryCriterionAndActiveNoOnPart__Then_RaiseEvent()
        {
            var caseInstanceId = Guid.NewGuid();
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var stage = new Stage();

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = stage.Id,
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        SentryRef = sentryDefinitionId
                    }
                }
            };

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
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

            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_EntryCriterionAndActiveNoOnPartNotRepeatable__Then_RaiseEvent()
        {
            var caseInstanceId = Guid.NewGuid();
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var stage = new Stage();

            var repetitionRule = Rules.NotRepeatableRule;

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = stage.Id,
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

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain.Setup(x => x.ExecuteAsBool(repetitionRule.ContextRef, repetitionRule.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Success(false)));

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
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            RepetitionRuleEvaluated capturedRule = null;
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()))
                .Callback<RepetitionRuleEvaluated>(x => capturedRule = x);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
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

            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_EntryCriterionAndActiveNoOnPartIsRepeatable__Then_RaiseEventAndRepeat()
        {
            var caseInstanceId = Guid.NewGuid();
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var stage = new Stage();

            var repetitionRule = Rules.IsRepeatableRule;

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = stage.Id,
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

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);

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
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            RepetitionRuleEvaluated capturedRule = null;
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()))
                .Callback<RepetitionRuleEvaluated>(x => capturedRule = x);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
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

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()), Times.Once);
            capturedRule.Should().NotBeNull();
            capturedRule.Result.Should().BeTrue();

            mockHost.Verify(x => x.UnsubscribeFrom<SentrySatisfiedEvent>(sentryDefinitionId), Times.Once);
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Once);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Once);

            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_ExitCriterion__Then_RaiseEventAndExit()
        {
            var caseInstanceId = Guid.NewGuid();
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var stage = new Stage();

            var repetitionRule = Rules.IsRepeatableRule;

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = stage.Id,
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = repetitionRule
                },
                ExitCriteria =
                {
                    new ExitCriterion
                    {
                        SentryRef = sentryDefinitionId
                    }
                }
            };

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Active);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        true),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ExitCriterionSatisfied>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Exit), Times.Once);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_PreviouslyRepeated__Then_RaiseEventDoNotStart()
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

            var stage = new Stage();

            var testStore = new TestPlanItemStore(piDef: stage);
            testStore.Apply(new Repeated());

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
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
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Start), Times.Never);
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Never);
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_EntryCriterionWithOnPartManuallyActivated__Then_RaiseEventAndEnable()
        {
            var caseInstanceId = Guid.NewGuid();
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var stage = new Stage();

            var manualActivationRule = Rules.IsManuallyActivated;

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = stage.Id,
                ItemControl = new PlanItemControl
                {
                    ManualActivationRule = manualActivationRule
                },
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        SentryRef = sentryDefinitionId
                    }
                }
            };

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Uninitialized);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain.Setup(x => x.ExecuteAsBool(manualActivationRule.ContextRef, manualActivationRule.Condition))
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
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            ManualActivationRuleEvaluated capturedRule = null;
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()))
                .Callback<ManualActivationRuleEvaluated>(x => capturedRule = x);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
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

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()), Times.Once);
            capturedRule.Should().NotBeNull();
            capturedRule.Result.Should().BeTrue();

            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Create));
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Enable));
        }

        [Fact]
        public async Task HandleSentrySatisfied__When_EntryCriterionWithOnPartNotManuallyActivated__Then_RaiseEventAndStart()
        {
            var caseInstanceId = Guid.NewGuid();
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var stage = new Stage();

            var manualActivationRule = Rules.NotManuallyActivated;

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = stage.Id,
                ItemControl = new PlanItemControl
                {
                    ManualActivationRule = manualActivationRule
                },
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        SentryRef = sentryDefinitionId
                    }
                }
            };

            var testStore = new TestPlanItemStore(piDef: stage, def: pi, initialState: PlanItemState.Uninitialized);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain.Setup(x => x.ExecuteAsBool(manualActivationRule.ContextRef, manualActivationRule.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Success(false)));

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
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            ManualActivationRuleEvaluated capturedRule = null;
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()))
                .Callback<ManualActivationRuleEvaluated>(x => capturedRule = x);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new StageBehavior(mockHost.Object, stage, mockMachine.Object);

            await (Task)typeof(StageBehavior)
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

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()), Times.Once);
            capturedRule.Should().NotBeNull();
            capturedRule.Result.Should().BeFalse();

            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Create));
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Start));
        }
    }
}
