using System;
using System.Reflection;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Expressions;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Plan.PlanItem.StateMachine;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Plan.PlanItem.Behaviors
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

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

            var result = subject.GetItemControl();

            result.Should().Be(expectedItemControl);
        }

        [Fact]
        public void GetItemControl__Given_Host__When_HostDefinitionNotHaveItemControl__Then_GetDefItemControl()
        {
            var expectedItemControl = new PlanItemControl();
            var planItem = new Interfaces.Model.PlanItem();

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            var def = new Milestone
            {
                DefaultControl = expectedItemControl
            };

            var subject = new BaseBehaviorTestHarness(mockHost.Object, def, mockMachine.Object);

            var result = subject.GetItemControl();

            result.Should().Be(expectedItemControl);
        }

        [Fact]
        public async Task EvaluateRequiredRule__Given_Host__When_NoItemControl__Then_False()
        {
            var planItem = new Interfaces.Model.PlanItem();

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            RequiredRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<RequiredRuleEvaluated>()))
                .Callback<RequiredRuleEvaluated>(x => capturedEvent = x);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

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

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.Definition)
                .Returns(planItem);

            RequiredRuleEvaluated capturedEvent = null;
            mockHost
                .Setup(x => x.RaiseEvent(It.IsAny<RequiredRuleEvaluated>()))
                .Callback<RequiredRuleEvaluated>(x => capturedEvent = x);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

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

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

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

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

            await subject.EvaluateRequiredRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RequiredRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeTrue();
        }

        // #158 companion - RequiredRule's spec default is FALSE (5.5.1: "If this rule is not
        // present, then it is considered FALSE"), which happens to coincide with
        // ExecutableResult<bool>'s failure-value default of false. So this call site was never
        // observably wrong, but the fix in EvaluateRule must take this path via the explicit
        // IsError check and defaultResult fallback - not via Value coincidentally being false -
        // so this locks in the intended behavior rather than relying on happenstance.
        [Theory, AutoData]
        public async Task EvaluateRequiredRule__Given_Host__When_ConditionErrors__Then_DefaultResultFalse(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl
                {
                    RequiredRule = Rules.ErroringRequiredRule
                }
            };

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            // #194 - EvaluateRule's error branch calls Host.LogWithContext(Action<ILogger>), not a
            // logger it owns directly, so the seam to capture it is intercepting that callback and
            // invoking it against a FakeLogger instead of production's real ILogger - IBehaviorHost
            // is mocked in this test class, there is no DI container / ILoggerProvider to hook.
            var fakeLogger = new FakeLogger();
            mockHost
                .Setup(x => x.LogWithContext(It.IsAny<Action<ILogger>>()))
                .Callback<Action<ILogger>>(logAction => logAction(fakeLogger));

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsBool(Rules.ErroringRequiredRule.ContextRef, Rules.ErroringRequiredRule.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Failure("expression blew up")));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);
            mockHost
                .Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

            await subject.EvaluateRequiredRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RequiredRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeFalse();
            capturedEvent.Error.Should().Be("expression blew up");

            // #194 - the audit event above proves the error is recorded in the journal; this proves
            // it is ALSO independently observable via logging, so a future edit that drops
            // EvaluateRule's LogError branch (while leaving the event alone) still fails a test.
            fakeLogger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
                .Which.Message.Should().Contain("expression blew up");
        }

        [Theory, AutoData]
        public async Task EvaluateRepetitionRule__Given_Host__When_NoItemControl__Then_False(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem();

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

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

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

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

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

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

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

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

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

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

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

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

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

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

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

            await subject.EvaluateManualActivationRule();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Never);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeTrue();
        }

        // #158 - fail-open defect. 5.5.1: "If no ManualActivationRule is specified, then the
        // default is considered TRUE." When the rule IS specified but its Condition expression
        // ERRORS (as opposed to evaluating cleanly to false), EvaluateRule must fall back to that
        // same spec default rather than silently treating the error as a false result -
        // ExecutableResult<bool>.Failure(...) leaves Value at default(bool) == false, which is a
        // DIFFERENT thing than "the expression evaluated to false", and must not be allowed to
        // masquerade as it. On unfixed code this test fails: capturedEvent.Result/the return value
        // come back False (the ExecutableResult's default(bool)), silently bypassing the manual/
        // human activation gate described in issue #158.
        [Theory, AutoData]
        public async Task EvaluateManualActivationRule__Given_Host__When_ConditionErrors__Then_DefaultResultTrue(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl
                {
                    ManualActivationRule = Rules.ErroringManualActivationRule
                }
            };

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            // #194 - see the identical setup in EvaluateRequiredRule__...ConditionErrors above for
            // why intercepting Host.LogWithContext's callback is the right seam here.
            var fakeLogger = new FakeLogger();
            mockHost
                .Setup(x => x.LogWithContext(It.IsAny<Action<ILogger>>()))
                .Callback<Action<ILogger>>(logAction => logAction(fakeLogger));

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsBool(Rules.ErroringManualActivationRule.ContextRef, Rules.ErroringManualActivationRule.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Failure("expression blew up")));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);
            mockHost
                .Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

            var result = await subject.EvaluateManualActivationRule();

            result.Should().BeTrue(
                "an erroring expression must fall back to ManualActivationRule's spec default of TRUE, " +
                "not the ExecutableResult<bool> failure-value default of false - a false result here means " +
                "the human/manual activation gate is silently bypassed (#158)");

            // #194 - proves the error is independently observable via logging, not just recoverable
            // from the RaiseEvent'd audit event asserted below.
            fakeLogger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
                .Which.Message.Should().Contain("expression blew up");

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ManualActivationRuleEvaluated>()), Times.Once);

            capturedEvent.Should().NotBeNull();
            capturedEvent.Result.Should().BeTrue();
            capturedEvent.Error.Should().Be("expression blew up");
        }

        // 8.6.4 RepetitionRule
        // ~~~~~
        // "The first time a Milestone, Stage, or Task instance is instantiated and transitions to
        // the Available state it is not considered a repetition, nevertheless the RepetitionRule
        // MUST be evaluated and its result discarded." The discard flag rides the
        // RepetitionRuleEvaluated event so the store can skip persisting Result as Repeatable
        // (see PlanItemStoreTests/CaseStoreTests) while the evaluation itself still happens.
        [Theory, AutoData]
        public async Task EvaluateRepetitionRule__Given_Discard__Then_EventFlaggedDiscarded(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = Rules.IsRepeatableRule
                }
            };

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

            // the rule is still evaluated (the spec demands evaluation, only the RESULT is
            // discarded) and the raised event still carries the Result for audit purposes
            var result = await subject.EvaluateRepetitionRule(discard: true);

            result.Should().BeTrue("the evaluation itself still happens and still returns its value");

            capturedEvent.Should().NotBeNull();
            capturedEvent.Discard.Should().BeTrue("the first (Create -> Available) evaluation must be flagged so stores discard its Result");
            capturedEvent.Result.Should().BeTrue();
        }

        [Theory, AutoData]
        public async Task EvaluateRepetitionRule__Given_DefaultCall__Then_EventNotFlaggedDiscarded(Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = Rules.IsRepeatableRule
                }
            };

            var mockMachine = new MockPlanItemStateMachine(CreateStore(def: planItem));

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

            var subject = new BaseBehaviorTestHarness(mockHost.Object, new Milestone(), mockMachine.Object);

            await subject.EvaluateRepetitionRule();

            capturedEvent.Should().NotBeNull();
            capturedEvent.Discard.Should().BeFalse("re-evaluations (entry-criterion satisfaction, complete/terminate) are real, persistable evaluations");
        }

        private PlanItemStore CreateStore(
            string caseDefId = null,
            PlanItemDefinition piDef = null,
            Interfaces.Model.PlanItem def = null,
            PlanItemState initialState = PlanItemState.Available)
        {
            caseDefId = caseDefId ?? Guid.NewGuid().ToString();
            piDef = piDef ?? new Milestone { Id = "Milestone" };
            def = def ?? new Interfaces.Model.PlanItem
            {
                Id = "PlanItem",
                DefinitionRef = piDef.Id
            };

            var store = new PlanItemStore();
            store.Apply(new Defined
            {
                CaseDefinitionId = caseDefId,
                PlanItemDefinition = piDef,
                Definition = def
            });
            store.Apply(new Transitioned
            {
                Destination = initialState
            });
            return store;
        }

        private class BaseBehaviorTestHarness : BaseBehavior<Milestone>
        {
            public BaseBehaviorTestHarness(IBehaviorHost host, Milestone planItemDefinition, IPlanItemStateMachine stateMachine) :
                base(host, planItemDefinition, stateMachine)
            {
            }

            protected override Task HandleSentrySatisfied(SentrySatisfiedEvent @event, StreamSequenceToken token = null) => Task.CompletedTask;
            protected override Task HandleParentTransitioned(PlanItemTransitionedEvent @event, StreamSequenceToken token = null) => Task.CompletedTask;

            public new Task EvaluateRequiredRule() => base.EvaluateRequiredRule();
            public new Task<bool> EvaluateRepetitionRule(bool discard = false) => base.EvaluateRepetitionRule(discard);
            public new Task<bool> EvaluateManualActivationRule() => base.EvaluateManualActivationRule();

            public PlanItemControl GetItemControl()
            {
                var mi = GetType().BaseType.GetMethod(nameof(GetItemControl), BindingFlags.Instance | BindingFlags.NonPublic);
                return (PlanItemControl)mi.Invoke(this, Array.Empty<object>());
            }
        }
    }
}
