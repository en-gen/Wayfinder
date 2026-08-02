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

        // #216 phase 1 (characterization) - THE asymmetry guard.
        //
        // Table 8.9's `complete` rows carry separate columns per child type. Stage and Task
        // instances in {Available, Enabled, Active, Suspended} are `<impossible>` alongside a
        // Completed parent (hence #179's Exit cascade in StageBehavior/TaskBehavior), but Milestone
        // and EventListener instances in Available/Suspended are explicitly permitted to REMAIN
        // Available/Suspended - they legitimately survive a completed parent. Table 8.7's own
        // description of a completed Stage confirms it: it names only "Stage or Task instances" as
        // needing to be Completed/Terminated, conspicuously omitting Milestone/EventListener.
        //
        // So MilestoneBehavior.HandleParentTransitioned deliberately has NO `complete` arm, and
        // adding one - however natural it looks next to Stage's - would itself be a Table 8.9
        // violation, just in the opposite direction. Before this test, `ParentCompleted` had ZERO
        // coverage repo-wide, so giving Milestone a Complete arm would have left the entire suite
        // green. This is the test that must go red if that happens.
        //
        // Host.State is wired (unlike this file's other parent-transition tests) precisely so that
        // such a mutation fails on the ASSERTIONS below rather than on an NRE reading
        // Host.State.PlanItemState - a red for the right reason.
        [Theory]
        [InlineData(PlanItemState.Available)]
        [InlineData(PlanItemState.Suspended)]
        public async Task HandleParentTransitioned__When_ParentCompleted__Then_NoEventAndNoTransition
            (PlanItemState hostState)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var milestone = new Milestone();

            var testStore = new TestPlanItemStore(piDef: milestone, def: pi, initialState: hostState);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

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
                        PlanItemTransition.Complete,
                        PlanItemState.Active,
                        PlanItemState.Completed),
                    (StreamSequenceToken)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<ParentCompleted>()), Times.Never,
                "Table 8.9 permits a Milestone child to survive a completing parent - giving MilestoneBehavior a Complete arm would be a conformance violation");
            mockMachine.Verify(x => x.FireAsync(It.IsAny<PlanItemTransition>()), Times.Never);

            mockMachine.Object.State.Should().Be(hostState,
                "Table 8.9's `complete` row keeps a Milestone child Available -> Available and Suspended -> Suspended");
            testStore.PlanItemState.Should().Be(hostState);
        }

        // #216 phase 1 (characterization) - Table 8.9's parent-transition column for a Milestone
        // child, made executable rather than a comment.
        //
        // The per-type vocabulary here is genuinely irregular and is exactly the kind of detail a
        // consolidation into BaseBehavior could flatten by accident: a Milestone reacts to its
        // parent suspending/resuming with its OWN Suspend/Resume triggers (Table 8.11 gives it no
        // ParentSuspend/ParentResume at all - ConfigureForMilestoneOrEventListener permits none),
        // and to its parent exiting/terminating with ParentTerminate - where Stage/Task use
        // ParentSuspend/ParentResume/Exit for the same three rows.
        //
        // Reactivate is included because it is the CasePlanModel's own way out of Suspended (Table
        // 8.6 has no Resume edge for the Case at all - #63 D8), so a direct Milestone child of the
        // case would otherwise never see a transition its switch recognizes. No existing test
        // covered that arm on this type.
        [Theory]
        [InlineData(PlanItemTransition.Suspend, PlanItemState.Available, PlanItemTransition.Suspend, PlanItemState.Suspended)]
        [InlineData(PlanItemTransition.ParentSuspend, PlanItemState.Available, PlanItemTransition.Suspend, PlanItemState.Suspended)]
        [InlineData(PlanItemTransition.Resume, PlanItemState.Suspended, PlanItemTransition.Resume, PlanItemState.Available)]
        [InlineData(PlanItemTransition.ParentResume, PlanItemState.Suspended, PlanItemTransition.Resume, PlanItemState.Available)]
        [InlineData(PlanItemTransition.Reactivate, PlanItemState.Suspended, PlanItemTransition.Resume, PlanItemState.Available)]
        [InlineData(PlanItemTransition.Exit, PlanItemState.Available, PlanItemTransition.ParentTerminate, PlanItemState.Terminated)]
        [InlineData(PlanItemTransition.Terminate, PlanItemState.Available, PlanItemTransition.ParentTerminate, PlanItemState.Terminated)]
        public async Task HandleParentTransitioned__When_ParentTransitioned__Then_FiresThisTypesTable89Trigger(
            PlanItemTransition parentTransition,
            PlanItemState hostState,
            PlanItemTransition expectedOwnTrigger,
            PlanItemState expectedDestination)
        {
            var parentInstanceId = ShortGuid.NewGuid();

            var pi = new Interfaces.Model.PlanItem();

            var milestone = new Milestone();

            var testStore = new TestPlanItemStore(piDef: milestone, def: pi, initialState: hostState);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.ParentInstanceId)
                .Returns(parentInstanceId);
            mockHost.Setup(x => x.Definition)
                .Returns(pi);
            mockHost.Setup(x => x.State)
                .Returns(testStore);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => testStore.Apply((dynamic)x));

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

            mockMachine.Verify(x => x.FireAsync(expectedOwnTrigger), Times.Once);

            // Asserted alongside the FireAsync verification, not instead of it: a wrong trigger
            // that happens to be unpermitted from this state would otherwise no-op silently
            // (HandleParentTransitioned gates every fire on CanFire), leaving only the destination
            // state to catch it.
            mockMachine.Object.State.Should().Be(expectedDestination);
            testStore.PlanItemState.Should().Be(expectedDestination);
        }
    }
}
