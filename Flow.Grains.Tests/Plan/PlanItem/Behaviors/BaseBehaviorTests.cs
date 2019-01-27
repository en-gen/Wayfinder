using System;
using System.Reflection;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Events;
using Flow.Grains.Executables;
using Flow.Grains.Expressions;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Tests.Helpers;
using FluentAssertions;
using Moq;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.Behaviors
{
    public class BaseBehaviorTests
    {
        [Fact]
        public void GetItemControl__Given_Host__When_HostDefinitionHasItemControl__Then_GetHostItemControl()
        {
            var expectedItemControl = new PlanItemControl();
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = expectedItemControl
            };

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            var result = subject.GetItemControl();

            result.Should().Be(expectedItemControl);
        }

        [Fact]
        public void GetItemControl__Given_Host__When_HostDefinitionNotHaveItemControl__Then_GetDefItemControl()
        {
            var expectedItemControl = new PlanItemControl();
            var planItem = new Interfaces.Model.PlanItem();

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            var def = new Milestone
            {
                DefaultControl = expectedItemControl
            };

            var subject = new BaseBehaviorTestHarness(mockHost.Object, def);

            var result = subject.GetItemControl();

            result.Should().Be(expectedItemControl);
        }

        [Fact]
        public async Task EvaluateRequiredRule__Given_Host__When_NoItemControl__Then_False()
        {
            var planItem = new Interfaces.Model.PlanItem();

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            RequiredRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<RequiredRuleEvaluated>()))
                .Callback<RequiredRuleEvaluated>(x => capturedEvent = x);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            await subject.EvaluateRequiredRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RequiredRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeFalse();
        }

        [Fact]
        public async Task EvaluateRequiredRule__Given_Host__When_ItemControlWithoutRequiredRule__Then_False()
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl()
            };

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            RequiredRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<RequiredRuleEvaluated>()))
                .Callback<RequiredRuleEvaluated>(x => capturedEvent = x);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            await subject.EvaluateRequiredRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RequiredRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task EvaluateRequiredRule__Given_Host__When_ItemControlWithFalsyRequiredRule__Then_False(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl
                {
                    RequiredRule = Rules.NotRequiredRule
                }
            };

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            RequiredRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<RequiredRuleEvaluated>()))
                .Callback<RequiredRuleEvaluated>(x => capturedEvent = x);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsBool(Rules.NotRequiredRule.ContextRef, Rules.NotRequiredRule.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Success(false)));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);
            mockHost
                .Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            await subject.EvaluateRequiredRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RequiredRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task EvaluateRequiredRule__Given_Host__When_ItemControlWithTruthyRequiredRule__Then_True(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl
                {
                    RequiredRule = Rules.IsRequiredRule
                }
            };

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            RequiredRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<RequiredRuleEvaluated>()))
                .Callback<RequiredRuleEvaluated>(x => capturedEvent = x);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsBool(Rules.IsRequiredRule.ContextRef, Rules.IsRequiredRule.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Success(true)));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);
            mockHost
                .Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            await subject.EvaluateRequiredRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RequiredRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeTrue();
        }

        [Theory, AutoData]
        public async Task EvaluateRepetitionRule__Given_Host__When_NoItemControl__Then_False(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem();

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            RepetitionRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()))
                .Callback<RepetitionRuleEvaluated>(x => capturedEvent = x);
            
            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            await subject.EvaluateRepetitionRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Never);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task EvaluateRepetitionRule__Given_Host__When_ItemControlWithoutRepetitionRule__Then_False(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl()
            };

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            RepetitionRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()))
                .Callback<RepetitionRuleEvaluated>(x => capturedEvent = x);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            await subject.EvaluateRepetitionRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Never);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task EvaluateRepetitionRule__Given_Host__When_ItemControlWithFalsyRepetitionRule__Then_False(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = Rules.NotRepeatableRule
                }
            };

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            RepetitionRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()))
                .Callback<RepetitionRuleEvaluated>(x => capturedEvent = x);
            
            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsBool(Rules.NotRepeatableRule.ContextRef, Rules.NotRepeatableRule.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Success(false)));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);
            mockHost
                .Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            await subject.EvaluateRepetitionRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Never);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task EvaluateRepetitionRule__Given_Host__When_ItemControlWithTruthyRepetitionRule__Then_True(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = Rules.IsRepeatableRule
                }
            };

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            RepetitionRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()))
                .Callback<RepetitionRuleEvaluated>(x => capturedEvent = x);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsBool(Rules.IsRepeatableRule.ContextRef, Rules.IsRepeatableRule.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Success(true)));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);
            mockHost
                .Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            await subject.EvaluateRepetitionRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Never);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeTrue();
        }

        [Theory, AutoData]
        public async Task EvaluateManualActivationRule__Given_Host__When_NoItemControl__Then_True(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem();

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            ManualActivationRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()))
                .Callback<ManualActivationRuleEvaluated>(x => capturedEvent = x);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            await subject.EvaluateManualActivationRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Never);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeTrue();
        }

        [Theory, AutoData]
        public async Task EvaluateManualActivationRule__Given_Host__When_ItemControlWithoutManualActivationRule__Then_True(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl()
            };

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            ManualActivationRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()))
                .Callback<ManualActivationRuleEvaluated>(x => capturedEvent = x);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            await subject.EvaluateManualActivationRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Never);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeTrue();
        }

        [Theory, AutoData]
        public async Task EvaluateManualActivationRule__Given_Host__When_ItemControlWithFalsyManualActivationRule__Then_False(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl
                {
                    ManualActivationRule = Rules.NotManuallyActivated
                }
            };

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            ManualActivationRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()))
                .Callback<ManualActivationRuleEvaluated>(x => capturedEvent = x);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsBool(Rules.NotManuallyActivated.ContextRef, Rules.NotManuallyActivated.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Success(false)));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);
            mockHost
                .Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            await subject.EvaluateManualActivationRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Never);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task EvaluateManualActivationRule__Given_Host__When_ItemControlWithTruthyManualActivationRule__Then_True(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl
                {
                    ManualActivationRule = Rules.IsManuallyActivated
                }
            };

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            ManualActivationRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()))
                .Callback<ManualActivationRuleEvaluated>(x => capturedEvent = x);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsBool(Rules.IsManuallyActivated.ContextRef, Rules.IsManuallyActivated.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Success(true)));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);
            mockHost
                .Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone());

            await subject.EvaluateManualActivationRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Never);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeTrue();
        }

        private class BaseBehaviorTestHarness : BaseBehavior<Milestone>
        {
            public BaseBehaviorTestHarness(IBehaviorHost host, Milestone planItemDefinition) :
                base(host, planItemDefinition)
            {
            }

            public override Task Define() => Task.CompletedTask;
            public override Task Activate() => Task.CompletedTask;
            public override Task<bool> IsUserCompletable() => Task.FromResult(false);
            protected override Task HandleSentrySatisfied(SentrySatisfiedEvent @event, StreamSequenceToken token = null) => Task.CompletedTask;

            public new Task EvaluateRequiredRule() => base.EvaluateRequiredRule();
            public new Task<bool> EvaluateRepetitionRule() => base.EvaluateRepetitionRule();
            public new Task<bool> EvaluateManualActivationRule() => base.EvaluateManualActivationRule();

            public PlanItemControl GetItemControl()
            {
                var mi = GetType().BaseType.GetMethod(nameof(GetItemControl), BindingFlags.Instance | BindingFlags.NonPublic);
                return (PlanItemControl) mi.Invoke(this, Array.Empty<object>());
            }
        }
    }
}
