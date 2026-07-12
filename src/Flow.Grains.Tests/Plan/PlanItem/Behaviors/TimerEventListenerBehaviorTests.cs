using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Executables;
using Flow.Grains.Expressions;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.PlanItem.Events;
using Flow.Grains.Scheduler;
using Flow.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Moq;
using Orleans;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.Behaviors
{
    public class TimerEventListenerBehaviorTests
    {
        // Bug #61 discipline (entry-action raise): HandleEnterAvailableFromCreate is wired via
        // .OnEntryFromAsync(PlanItemTransition.Create, ...) - i.e. it runs AFTER
        // BaseBehavior.HandleTransitioned has already raised-and-confirmed the Transitioned event
        // for this Create transition (Stateless invokes the transition callback before the
        // destination state's entry actions). EvaluateTimerExpression (called from this entry
        // action) raises TimerExpressionEvaluated with no confirm of its own, relying on a later
        // Fault cascade to flush it - but ConfigureForMilestoneOrEventListener (the state-machine
        // shape TimerEventListener uses, being an EventListener) defines NO Fault permit at all,
        // so that fallback can never fire for this behavior. Absent an explicit confirm, this
        // event sits queued in TentativeState indefinitely and is never persisted/visible - lost
        // outright if the grain deactivates before anything else happens to confirm.
        //
        // Asserting plain Times.AtLeastOnce on ConfirmEvents() would NOT catch a regression here:
        // BaseBehavior.HandleTransitioned unconditionally confirms the Transitioned event for
        // every transition, including this Create - but that confirm runs BEFORE this entry
        // action (per Stateless's callback-before-entry-action ordering), so it happens too early
        // to flush anything HandleEnterAvailableFromCreate itself raises. The assertion below
        // records call ORDER and requires a ConfirmEvents() AFTER the TimerExpressionEvaluated
        // raise specifically, which is the one BaseBehavior.HandleTransitioned's earlier confirm
        // cannot satisfy.
        [Fact]
        public async Task Trigger__Given_TimerExpression__When_Create__Then_TimerExpressionEvaluatedConfirmedAfterRaise()
        {
            var caseInstanceId = Guid.NewGuid();
            var instanceId = ShortGuid.NewGuid();

            var timerExpression = new Expression
            {
                Id = Guid.NewGuid().ToString(),
                Language = ExpressionLanguage.Jint,
                Body = "'PT1H'"
            };

            var timerEventListener = new TimerEventListener
            {
                Id = "TimerA",
                TimerExpression = timerExpression
                // TimerStart intentionally left null: takes the "schedule immediately" branch of
                // HandleEnterAvailableFromCreate's switch, which raises nothing further - isolating
                // the assertion to EvaluateTimerExpression's own raise.
            };

            var pi = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemTimerA",
                DefinitionRef = timerEventListener.Id
            };

            var testStore = new TestPlanItemStore(
                def: pi,
                piDef: timerEventListener,
                initialState: PlanItemState.Uninitialized);

            var evaluatedSchedule = new Iso8601("PT1H");

            var mockExpressionGrain = new Mock<IExpressionGrain>();
            mockExpressionGrain
                .Setup(x => x.ExecuteAsIso8601(null, timerExpression))
                .Returns(Task.FromResult(ExecutableResult<Iso8601>.Success(evaluatedSchedule)));

            var mockTimerSchedulerGrain = new Mock<ITimerEventSchedulerGrain>();

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<IExpressionGrain>(caseInstanceId, null))
                .Returns(mockExpressionGrain.Object);
            mockGrainFactory
                .Setup(x => x.GetGrain<ITimerEventSchedulerGrain>(caseInstanceId, null))
                .Returns(mockTimerSchedulerGrain.Object);

            // records the order calls happen in, so we can tell an EARLIER confirm (e.g. the one
            // BaseBehavior.HandleTransitioned issues for the Create transition itself, before any
            // entry action runs) apart from a confirm that actually flushes THIS raise.
            var callLog = new List<string>();

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.InstanceId).Returns(instanceId);
            mockHost.Setup(x => x.Definition).Returns(pi);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => callLog.Add($"Raise:{x.GetType().Name}"));
            mockHost.Setup(x => x.ConfirmEvents())
                .Callback(() => callLog.Add("Confirm"))
                .Returns(Task.CompletedTask);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TimerEventListenerBehavior(mockHost.Object, timerEventListener, mockMachine.Object);

            await subject.Trigger(PlanItemTransition.Create);

            var raiseIndex = callLog.IndexOf($"Raise:{nameof(TimerExpressionEvaluated)}");
            raiseIndex.Should().BeGreaterOrEqualTo(0, "EvaluateTimerExpression should have raised TimerExpressionEvaluated");

            // no later transition in this call rides a confirm for this raise (Fault is
            // unreachable for EventListener-shaped state machines - no Permit(Fault, ...) exists
            // in ConfigureForMilestoneOrEventListener), so without HandleEnterAvailableFromCreate's
            // own explicit confirm this event would sit queued in TentativeState forever.
            callLog.Skip(raiseIndex + 1).Should()
                .Contain("Confirm", "TimerExpressionEvaluated must be confirmed after it is raised, not just before it (in the Transitioned confirm) or never");
        }
    }
}
