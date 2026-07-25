using System;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Expressions;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Tests.Utils.Helpers;
using Moq;
using Orleans;
using Xunit;

namespace Wayfinder.Grains.Tests.Plan.PlanItem.Behaviors
{
    // 8.6.4 RepetitionRule / 5.4.11.3 (D7)
    // ~~~~~
    // "Stage and Task instances with a RepetitionRule that do not have any entry criteria, will
    // try to create a new instance every time an instance transitions into the Complete or
    // Terminate state. Under that condition the RepetitionRule is re-evaluated and if the
    // Expression evaluates to TRUE, a new instance is created."
    //
    // These tests drive the REAL PlanItemStateMachine (MockPlanItemStateMachine forwards
    // Configure/FireAsync to a real instance, so entry actions registered by the behavior under
    // test actually execute) from Active into Completed/Terminated and assert on the observable
    // outcome: a PlanItemRepetitionCriteriaMetEvent published to the parent scope plus a Repeated
    // event raised - or, for the excluded shapes (entry criteria present, no rule, rule false,
    // Milestone, CasePlanModel), nothing.
    public class RepeatOnCompleteOrTerminateTests
    {
        [Theory]
        [InlineData(PlanItemTransition.Complete)]
        [InlineData(PlanItemTransition.Terminate)]
        public async Task Trigger__Given_NoEntryCriteriaRepeatableTask__When_CompleteOrTerminate__Then_RepetitionCriteriaMetPublished(PlanItemTransition transition)
        {
            var caseInstanceId = Guid.NewGuid();

            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = Rules.IsRepeatableRule
                }
            };
            var taskDefinition = new HumanTask { Id = "TaskA", IsBlocking = true };

            var testStore = new TestPlanItemStore(def: planItem, piDef: taskDefinition, initialState: PlanItemState.Active);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsBool(Rules.IsRepeatableRule.ContextRef, Rules.IsRepeatableRule.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Success(true)));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.Definition).Returns(planItem);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, taskDefinition, mockMachine.Object);

            await subject.Trigger(transition);

            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Once);
            // Bug #61 discipline: the events raised inside this state-entry action have no later
            // transition confirm to ride on and must be confirmed explicitly
            mockHost.Verify(x => x.ConfirmEvents(), Times.AtLeastOnce);
        }

        // Table 8.8: the re-evaluation note is attached to the "complete" and "terminate"
        // transitions only - the "exit" row (exit criteria satisfied, or propagation from an
        // outer Stage terminating) carries no such note. An Exit-triggered arrival in Terminated
        // must not re-evaluate; in particular a parent stage terminating (which cascades as Exit
        // to its children - HandleParentTransitioned) must never respawn the children it is
        // shutting down.
        [Fact]
        public async Task Trigger__Given_NoEntryCriteriaRepeatableTask__When_Exit__Then_NoRepetition()
        {
            var caseInstanceId = Guid.NewGuid();

            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = Rules.IsRepeatableRule
                }
            };
            var taskDefinition = new HumanTask { Id = "TaskA", IsBlocking = true };

            var testStore = new TestPlanItemStore(def: planItem, piDef: taskDefinition, initialState: PlanItemState.Active);

            var mockExpressionGrain = new Mock<IExpressionGrain>();

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.Definition).Returns(planItem);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, taskDefinition, mockMachine.Object);

            await subject.Trigger(PlanItemTransition.Exit);

            mockExpressionGrain.Verify(x => x.ExecuteAsBool(It.IsAny<string>(), It.IsAny<Expression>()), Times.Never);
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Never);
        }

        [Fact]
        public async Task Trigger__Given_NoEntryCriteriaRepeatableTask__When_RuleEvaluatesFalse__Then_NoRepetition()
        {
            var caseInstanceId = Guid.NewGuid();

            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = Rules.NotRepeatableRule
                }
            };
            var taskDefinition = new HumanTask { Id = "TaskA", IsBlocking = true };

            var testStore = new TestPlanItemStore(def: planItem, piDef: taskDefinition, initialState: PlanItemState.Active);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsBool(Rules.NotRepeatableRule.ContextRef, Rules.NotRepeatableRule.Condition))
                .Returns(Task.FromResult(ExecutableResult<bool>.Success(false)));

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.Definition).Returns(planItem);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, taskDefinition, mockMachine.Object);

            await subject.Trigger(PlanItemTransition.Complete);

            // the rule WAS re-evaluated (that is the D7 requirement) ...
            mockExpressionGrain.Verify(x => x.ExecuteAsBool(Rules.NotRepeatableRule.ContextRef, Rules.NotRepeatableRule.Condition), Times.Once);
            // ... but no repetition results from a FALSE evaluation
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Never);
        }

        // 8.6.4: the complete/terminate re-evaluation applies ONLY to items with NO entry
        // criteria - an item WITH entry criteria repeats via entry-criterion OnPart satisfaction
        // (HandleSentrySatisfied), never via its own completion.
        [Fact]
        public async Task Trigger__Given_RepeatableTaskWithEntryCriteria__When_Complete__Then_NoRepetitionAndNoEvaluation()
        {
            var caseInstanceId = Guid.NewGuid();

            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                EntryCriteria =
                {
                    new EntryCriterion { SentryRef = "SentryA" }
                },
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = Rules.IsRepeatableRule
                }
            };
            var taskDefinition = new HumanTask { Id = "TaskA", IsBlocking = true };

            var testStore = new TestPlanItemStore(def: planItem, piDef: taskDefinition, initialState: PlanItemState.Active);

            var mockExpressionGrain = new Mock<IExpressionGrain>();

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.Definition).Returns(planItem);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, taskDefinition, mockMachine.Object);

            await subject.Trigger(PlanItemTransition.Complete);

            mockExpressionGrain.Verify(x => x.ExecuteAsBool(It.IsAny<string>(), It.IsAny<Expression>()), Times.Never);
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Never);
        }

        [Fact]
        public async Task Trigger__Given_NoRepetitionRule__When_Complete__Then_NoRepetition()
        {
            var planItem = new Interfaces.Model.PlanItem { Id = "PlanItemA" };
            var taskDefinition = new HumanTask { Id = "TaskA", IsBlocking = true };

            var testStore = new TestPlanItemStore(def: planItem, piDef: taskDefinition, initialState: PlanItemState.Active);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Definition).Returns(planItem);
            mockHost.Setup(x => x.State).Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, taskDefinition, mockMachine.Object);

            await subject.Trigger(PlanItemTransition.Complete);

            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Never);
        }

        // 8.6.4 grants the complete/terminate re-spawn trigger to "Stage and Task instances"
        // only - a Milestone repeats exclusively via its entry criterion.
        [Fact]
        public async Task Trigger__Given_RepeatableMilestone__When_Occur__Then_NoRepetition()
        {
            var caseInstanceId = Guid.NewGuid();

            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = Rules.IsRepeatableRule
                }
            };
            var milestoneDefinition = new Milestone { Id = "MilestoneA" };

            var testStore = new TestPlanItemStore(def: planItem, piDef: milestoneDefinition, initialState: PlanItemState.Available);

            var mockExpressionGrain = new Mock<IExpressionGrain>();

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.Definition).Returns(planItem);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new MilestoneBehavior(mockHost.Object, milestoneDefinition, mockMachine.Object);

            // Milestone lifecycle: Available -[Occur]-> Completed (Table 8.10/8.11)
            await subject.Trigger(PlanItemTransition.Occur);

            mockExpressionGrain.Verify(x => x.ExecuteAsBool(It.IsAny<string>(), It.IsAny<Expression>()), Times.Never);
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Never);
        }

        // The outermost CasePlanModel Stage implements the CASE lifecycle (8.4.1/Table 8.6),
        // which defines no repetition semantics - completing a case must never attempt a repeat,
        // even if a (pathological) model attaches a RepetitionRule to the case plan model's
        // default control.
        [Fact]
        public async Task Trigger__Given_RepeatableCasePlanModel__When_Complete__Then_NoRepetition()
        {
            var caseInstanceId = Guid.NewGuid();

            var caseDefinition = new Interfaces.Model.Case();
            var casePlanModel = new Stage
            {
                Id = "CPM",
                IsCasePlanModel = true,
                DefaultControl = new PlanItemControl
                {
                    RepetitionRule = Rules.IsRepeatableRule
                }
            };

            var testStore = new TestPlanItemStore(piDef: casePlanModel, initialState: PlanItemState.Active);

            var mockExpressionGrain = new Mock<IExpressionGrain>();

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.Definition).Returns(caseDefinition);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new CasePlanModelBehavior(mockHost.Object, casePlanModel, mockMachine.Object);

            await subject.Trigger(PlanItemTransition.Complete);

            mockExpressionGrain.Verify(x => x.ExecuteAsBool(It.IsAny<string>(), It.IsAny<Expression>()), Times.Never);
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Never);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Never);
        }
    }
}
