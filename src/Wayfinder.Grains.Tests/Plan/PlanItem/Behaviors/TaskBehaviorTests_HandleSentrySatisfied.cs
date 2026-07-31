using System;
using System.Reflection;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Expressions;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
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
    public partial class TaskBehaviorTests
    {
        [Fact]
        public async Task HandleSentrySatisfied__When_OutOfScope__Then_Disregard()
        {
            var task = new HumanTask();

            var testStore = new TestPlanItemStore(piDef: task);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost
                .Setup(x => x.Scope)
                .Returns("scope");

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        "out_of_scope",
                        ShortGuid.NewGuid(),
                        true),
                    (StreamSequenceToken) null
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

            var task = new HumanTask();

            var testStore = new TestPlanItemStore(piDef: task);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        "not_a_match",
                        true),
                    (StreamSequenceToken) null
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

            var task = new HumanTask();

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = task.Id,
                EntryCriteria =
                {
                    new EntryCriterion
                    {
                        SentryRef = sentryDefinitionId
                    }
                }
            };

            var testStore = new TestPlanItemStore(piDef: task, def: pi, initialState: PlanItemState.Active);

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

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        false),
                    (StreamSequenceToken) null
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

            var task = new HumanTask();

            var repetitionRule = Rules.NotRepeatableRule;

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = task.Id,
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

            var testStore = new TestPlanItemStore(piDef: task, def: pi, initialState: PlanItemState.Active);

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

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        true),
                    (StreamSequenceToken) null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<EntryCriterionSatisfied>()), Times.Once);
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<Repeated>()), Times.Never);

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<RepetitionRuleEvaluated>()), Times.Once);
            capturedRule.Should().NotBeNull();
            capturedRule.Result.Should().BeFalse();

            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);
        }

        [Fact]
        public async Task
            HandleSentrySatisfied__When_EntryCriterionAndActiveNoOnPartIsRepeatable__Then_RaiseEventAndRepeat()
        {
            var caseInstanceId = Guid.NewGuid();
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var task = new HumanTask();

            var repetitionRule = Rules.IsRepeatableRule;

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = task.Id,
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

            var testStore = new TestPlanItemStore(piDef: task, def: pi, initialState: PlanItemState.Active);

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

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        true),
                    (StreamSequenceToken) null
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

            var task = new HumanTask();

            var repetitionRule = Rules.IsRepeatableRule;

            var exitCriterion = new ExitCriterion
            {
                SentryRef = sentryDefinitionId
            };

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = task.Id,
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = repetitionRule
                },
                ExitCriteria =
                {
                    exitCriterion
                }
            };

            var testStore = new TestPlanItemStore(piDef: task, def: pi, initialState: PlanItemState.Active);

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

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        true),
                    (StreamSequenceToken) null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ExitCriterionSatisfied>()), Times.Once);
            // ADO #183 - an ExitCriterion satisfaction must never also raise EntryCriterionSatisfied
            // (the regression this guards: HandleSentrySatisfied used to raise it unconditionally,
            // before branching on the criterion's actual type).
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<EntryCriterionSatisfied>()), Times.Never);
            // D10 (Bug #82) - the Exit trigger now carries the firing ExitCriterion's own Id so
            // SentryGrain can match a PlanItemOnPart naming it - see SentryGrain's class remarks.
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Exit, exitCriterion.Id), Times.Once);
        }

        // ADO #186 - unlike the CasePlanModel (StageBehaviorTests_HandleSentrySatisfied's own
        // repro), TaskBehavior's CanFire(Exit) gate cannot go false in STEADY STATE:
        // PlanItemStateMachine.ConfigureForStageOrTask permits Exit unconditionally from every
        // state where the ExitCriteria subscription is armed (Available/Enabled/Disabled/Active/
        // Suspended/Failed). The gap here is reachable only by a genuine delivery/unsubscribe
        // RACE - BaseBehavior.HandleEnterTerminal unsubscribes ExitCriteria on ENTRY to Completed/
        // Terminated, not before, so a SentrySatisfiedEvent already in flight when that entry
        // action runs can still be delivered on a LATER grain turn, after the Task has already
        // landed in Completed/Terminated. That interleaving (two overlapping grain turns racing
        // against a mid-flight unsubscribe) is not something this synchronous unit harness can
        // honestly reproduce - there is no way to invoke HandleSentrySatisfied "one turn late"
        // here. What CAN be pinned honestly is the postcondition the race produces: this method,
        // called with a genuine ExitCriterion satisfaction while the Task is ALREADY in a state
        // Table 8.8 permits no `exit` edge from (Completed has none in ConfigureForStageOrTask -
        // BaseBehavior only wires entry actions there, no Permits), must still journal the fact
        // and durably confirm it, exactly as StageBehavior's CasePlanModel repro does.
        [Fact]
        public async Task HandleSentrySatisfied__When_ExitCriterionButAlreadyCompleted__Then_JournaledButTransitionWithheld()
        {
            var caseInstanceId = Guid.NewGuid();
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var task = new HumanTask();

            var exitCriterion = new ExitCriterion
            {
                SentryRef = sentryDefinitionId
            };

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = task.Id,
                ExitCriteria =
                {
                    exitCriterion
                }
            };

            // Represents the state a stale, already-in-flight SentrySatisfiedEvent is delivered
            // into after the race described above - not a literal replay of the race's timing,
            // just its observable postcondition (same "set initialState directly" convention this
            // test file already uses throughout).
            var testStore = new TestPlanItemStore(piDef: task, def: pi, initialState: PlanItemState.Completed);

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

            mockMachine.Object.CanFire(PlanItemTransition.Exit).Should().BeFalse(
                "Table 8.8 permits no `exit` edge out of Completed - this is the precondition for the repro");

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        true),
                    (StreamSequenceToken) null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ExitCriterionSatisfied>()), Times.Once,
                "#186: a genuinely satisfied exit criterion must be journaled even when the resulting " +
                "transition cannot fire");
            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>(), It.IsAny<string>()), Times.Never);

            // BLOCKER caught in review: with no FireAsync, BaseBehavior.HandleTransitioned never
            // runs and never confirms - HandleSentrySatisfied's own trailing ConfirmEvents() is the
            // only thing standing between this raise and loss to an idle deactivation (#160).
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once,
                "#186: the journaled fact must be durably confirmed on the no-transition path");
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

            var task = new HumanTask();

            var testStore = new TestPlanItemStore(piDef: task);
            testStore.Apply(new Repeated());

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Scope)
                .Returns(scope);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        true),
                    (StreamSequenceToken) null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<EntryCriterionSatisfied>()), Times.Once);
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Start), Times.Never);
            mockHost.Verify(x => x.Publish(It.IsAny<PlanItemRepetitionCriteriaMetEvent>()), Times.Never);
        }

        [Fact]
        public async Task
            HandleSentrySatisfied__When_EntryCriterionWithOnPartManuallyActivated__Then_RaiseEventAndEnable()
        {
            var caseInstanceId = Guid.NewGuid();
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var task = new HumanTask();

            var manualActivationRule = Rules.IsManuallyActivated;

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = task.Id,
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

            var testStore = new TestPlanItemStore(piDef: task, def: pi, initialState: PlanItemState.Uninitialized);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain.Setup(x =>
                    x.ExecuteAsBool(manualActivationRule.ContextRef, manualActivationRule.Condition))
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

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        true),
                    (StreamSequenceToken) null
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
        public async Task
            HandleSentrySatisfied__When_EntryCriterionWithOnPartNotManuallyActivated__Then_RaiseEventAndStart()
        {
            var caseInstanceId = Guid.NewGuid();
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var task = new HumanTask();

            var manualActivationRule = Rules.NotManuallyActivated;

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = task.Id,
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

            var testStore = new TestPlanItemStore(piDef: task, def: pi, initialState: PlanItemState.Uninitialized);

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain.Setup(x =>
                    x.ExecuteAsBool(manualActivationRule.ContextRef, manualActivationRule.Condition))
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

            var subject = new TaskBehavior<HumanTask>(mockHost.Object, task, mockMachine.Object);

            await (Task)typeof(TaskBehavior<HumanTask>)
                .GetMethod("HandleSentrySatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    new SentrySatisfiedEvent(
                        scope,
                        sentryDefinitionId,
                        true),
                    (StreamSequenceToken) null
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
