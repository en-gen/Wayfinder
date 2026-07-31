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

            var exitCriterion = new ExitCriterion
            {
                SentryRef = sentryDefinitionId
            };

            var pi = new Interfaces.Model.PlanItem
            {
                DefinitionRef = stage.Id,
                ItemControl = new PlanItemControl
                {
                    RepetitionRule = repetitionRule
                },
                ExitCriteria =
                {
                    exitCriterion
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
            // ADO #183 - an ExitCriterion satisfaction must never also raise EntryCriterionSatisfied
            // (the regression this guards: HandleSentrySatisfied used to raise it unconditionally,
            // before branching on the criterion's actual type).
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<EntryCriterionSatisfied>()), Times.Never);
            // D10 (Bug #82) - the Exit trigger now carries the firing ExitCriterion's own Id so
            // SentryGrain can match a PlanItemOnPart naming it - see SentryGrain's class remarks.
            mockMachine.Verify(x => x.FireAsync(PlanItemTransition.Exit, exitCriterion.Id), Times.Once);
        }

        // ADO #186 - HandleSentrySatisfied's ExitCriterion branch used to be guarded by BOTH
        // "criterion is ExitCriterion" AND "StateMachine.CanFire(ExitCriterionTransition)", while
        // the EntryCriterion branch above is guarded only by the type test - the RaiseEvent sat
        // INSIDE the CanFire gate, so a genuine satisfaction that arrived while the transition
        // could not fire journaled nothing at all. For an ordinary Stage/Task,
        // PlanItemStateMachine.ConfigureForStageOrTask Permits Exit unconditionally from every
        // state where the ExitCriteria stream subscription is still live (Available, Enabled,
        // Disabled, Active, Suspended, Failed - see BaseBehavior.HandleEnterTerminal, which only
        // unsubscribes on Completed/Terminated entry), so CanFire(Exit) can never be false there
        // IN STEADY STATE - the gap is unreachable for that type absent a race. (It is still
        // reachable by a delivery/unsubscribe race: HandleEnterTerminal unsubscribes on ENTRY to
        // Completed/Terminated, not before, so a SentrySatisfiedEvent already in flight when that
        // entry action runs can still be delivered on a later grain turn, landing in a state where
        // CanFire(Exit) is false. That race is what actually justifies fixing TaskBehavior
        // alongside StageBehavior, even though only the latter has a steady-state repro below.)
        //
        // The CasePlanModel is a different story: CasePlanModelBehavior overrides
        // ExitCriterionTransition to Terminate (Table 8.6 has no `exit` row for it), and
        // PlanItemStateMachine.ConfigureForCasePlanModel's Suspended/Failed blocks permit ONLY
        // Reactivate and Close out of those states - NOT Terminate. Its ExitCriteria subscription
        // is armed once, on entry to Active from Create (CasePlanModelBehavior.
        // HandleEnterActiveFromCreate), and is never re-armed or torn down on Suspend/Fault - only
        // HandleEnterTerminal (Completed/Terminated/Closed entry) ever unsubscribes it. So a
        // genuinely satisfied exit criterion CAN reach this method IN STEADY STATE while
        // CanFire(Terminate) is false: SentryGrain.HandleOnPartOccurred/
        // HandleCaseWideCaseFileItemTransitioned publish SentrySatisfiedEvent purely from
        // OnPart/IfPart evaluation, with no check at all on the referencing PlanItem's own current
        // state - so nothing upstream of this method prevents the delivery. This is the scenario
        // that made the bug reachable without needing to contrive a race (proven here by driving
        // the CasePlanModel into Suspended/Failed for real before invoking the handler).
        //
        // Post-fix: the satisfaction must still be journaled (ExitCriterionSatisfied raised
        // exactly once) even though the transition itself correctly stays withheld (CanFire is
        // still false, so no FireAsync) - the fact and the transition are no longer coupled. The
        // journaling must also be DURABLE, not merely queued: see the trailing ConfirmEvents()
        // assertion below - HandleSentrySatisfied has no confirm on this exact no-FireAsync path
        // other than the one it now performs itself at the very end of the method.
        [Theory]
        [InlineData(PlanItemState.Suspended, PlanItemTransition.Suspend)]
        [InlineData(PlanItemState.Failed, PlanItemTransition.Fault)]
        public async Task HandleSentrySatisfied__When_CasePlanModelExitCriterionButCannotFireTerminate__Then_JournaledButTransitionWithheld(
            PlanItemState unreachableState, PlanItemTransition transitionIntoState)
        {
            var scope = ShortGuid.NewGuid();
            var sentryDefinitionId = ShortGuid.NewGuid();

            var casePlanModel = new Stage { Id = "CPM", IsCasePlanModel = true };

            var exitCriterion = new ExitCriterion { SentryRef = sentryDefinitionId };
            var caseDefinition = new Interfaces.Model.Case();
            caseDefinition.ExitCriteria.Add(exitCriterion);

            var testStore = new TestPlanItemStore(piDef: casePlanModel, initialState: PlanItemState.Active);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.Scope).Returns(scope);
            mockHost.Setup(x => x.Definition).Returns(caseDefinition);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new CasePlanModelBehavior(mockHost.Object, casePlanModel, mockMachine.Object);

            // Drive the CasePlanModel for real into a state where Table 8.6 permits no `terminate`
            // edge (Reactivate/Close only), while leaving the ExitCriteria subscription live -
            // nothing unsubscribes it outside of HandleEnterTerminal (Completed/Terminated/Closed).
            await mockMachine.Object.FireAsync(transitionIntoState);
            testStore.PlanItemState.Should().Be(unreachableState);
            mockMachine.Object.CanFire(PlanItemTransition.Terminate).Should().BeFalse(
                "Table 8.6 permits no `terminate` edge out of this state - this is the precondition for the repro");

            // Reset invocation history so the Verify calls below assert only what
            // HandleSentrySatisfied itself does, not the setup transition above (which already
            // drove its own RaiseEvent(Transitioned)/ConfirmEvents/Publish through HandleTransitioned).
            mockMachine.Invocations.Clear();
            mockHost.Invocations.Clear();

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

            // #186 fix: a genuine ExitCriterion satisfaction reached this method (the subscription
            // was live and the criterion matched) - it must be journaled regardless of whether the
            // resulting transition can fire, exactly like the EntryCriterion branch above.
            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ExitCriterionSatisfied>()), Times.Once,
                "#186: a genuinely satisfied exit criterion must be journaled even when the resulting " +
                "transition cannot fire - the fact and the transition are independent");

            // The transition itself must still be withheld: Table 8.6 permits no `terminate` edge
            // out of Suspended/Failed, and that part of the CanFire gate is correct and unchanged.
            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);
            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>(), It.IsAny<string>()), Times.Never);

            // BLOCKER caught in review: on this exact CanFire == false path, no FireAsync happens,
            // so BaseBehavior.HandleTransitioned never runs and never confirms - the raise above is
            // the last thing that happens. Without HandleSentrySatisfied's own trailing
            // ConfirmEvents(), the just-raised ExitCriterionSatisfied would sit unconfirmed in
            // TentativeState (#160) and be lost to an idle deactivation, achieving nothing over the
            // pre-fix silent drop. This is the assertion that actually proves durability.
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once,
                "#186: the journaled fact must be durably confirmed on the no-transition path, not merely " +
                "queued in TentativeState - otherwise it is lost exactly like #160 describes");
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
