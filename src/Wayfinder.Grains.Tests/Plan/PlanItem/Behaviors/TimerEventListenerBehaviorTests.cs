using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Expressions;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Scheduler;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Moq;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Plan.PlanItem.Behaviors
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

        // Bug #79 (sibling of #61 above, for the START-TRIGGER path): HandleStartTriggerOccurred is
        // reached via HandleStartTriggerSourceTransitioned, which is a stream-subscription
        // callback registered by HandleEnterAvailableFromCreate's PlanItemStartTrigger/
        // CaseFileItemStartTrigger branches (Host.SubscribeTo<PlanItemTransitionedEvent>(...)) -
        // NOT a state-machine transition callback. Nothing else in the pipeline confirms the
        // TimerStartTriggerOccurred event it raises, so without an explicit confirm it sits queued
        // in TentativeState and is lost outright if the grain deactivates before anything else
        // happens to confirm - the exact defect shape #61 fixed for the entry-action raise above.
        [Fact]
        public async Task HandleStartTriggerOccurred__Given_StartTriggerSourceTransitions__Then_TimerStartTriggerOccurredConfirmedAfterRaise()
        {
            var caseInstanceId = Guid.NewGuid();
            var instanceId = ShortGuid.NewGuid();

            var timerExpression = new Expression
            {
                Id = Guid.NewGuid().ToString(),
                Language = ExpressionLanguage.Jint,
                Body = "'PT1H'"
            };

            var startTrigger = new PlanItemStartTrigger
            {
                SourceRef = "SourcePlanItem",
                StandardEvent = PlanItemTransition.Complete
            };

            var timerEventListener = new TimerEventListener
            {
                Id = "TimerA",
                TimerExpression = timerExpression,
                TimerStart = startTrigger
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

            // records the order calls happen in, so we can tell "confirmed before the raise (or
            // never)" apart from "confirmed after the raise" - see the Skip(raiseIndex + 1)
            // assertion below.
            var callLog = new List<string>();
            Func<PlanItemTransitionedEvent, StreamSequenceToken, Task> capturedHandler = null;

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.InstanceId).Returns(instanceId);
            mockHost.Setup(x => x.Scope).Returns("PlanItemTimerA");
            mockHost.Setup(x => x.Definition).Returns(pi);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<object>()))
                .Callback<object>(x => callLog.Add($"Raise:{x.GetType().Name}"));
            mockHost.Setup(x => x.ConfirmEvents())
                .Callback(() => callLog.Add("Confirm"))
                .Returns(Task.CompletedTask);
            mockHost
                .Setup(x => x.SubscribeTo(
                    startTrigger.SourceRef,
                    It.IsAny<Func<PlanItemTransitionedEvent, StreamSequenceToken, Task>>(),
                    StreamFlags.Create))
                .Callback<string, Func<PlanItemTransitionedEvent, StreamSequenceToken, Task>, StreamFlags>(
                    (_, handler, _) => capturedHandler = handler)
                .Returns(Task.CompletedTask);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TimerEventListenerBehavior(mockHost.Object, timerEventListener, mockMachine.Object);

            // Create -> Available's entry action (HandleEnterAvailableFromCreate) evaluates the
            // timer expression (no Start component, so no conflict with TimerStart) and then, since
            // TimerStart is a PlanItemStartTrigger, subscribes to the source's transitioned stream -
            // capturing HandleStartTriggerOccurred's caller so we can invoke it directly below.
            await subject.Trigger(PlanItemTransition.Create);

            capturedHandler.Should().NotBeNull(
                "HandleEnterAvailableFromCreate's PlanItemStartTrigger branch must subscribe to the start-trigger source");

            callLog.Clear();

            // simulate the subscribed source plan item transitioning through the trigger's
            // StandardEvent - this is what actually invokes HandleStartTriggerOccurred in
            // production, via HandleStartTriggerSourceTransitioned.
            var triggerEvent = new PlanItemTransitionedEvent(
                "PlanItemTimerA",
                "SourcePlanItemInstance",
                startTrigger.SourceRef,
                startTrigger.StandardEvent,
                PlanItemState.Active,
                PlanItemState.Completed);

            await capturedHandler(triggerEvent, null);

            var raiseIndex = callLog.IndexOf($"Raise:{nameof(TimerStartTriggerOccurred)}");
            raiseIndex.Should().BeGreaterOrEqualTo(0, "HandleStartTriggerOccurred should have raised TimerStartTriggerOccurred");

            // no state-machine transition rides along with this stream-subscription callback to
            // confirm on its behalf - without HandleStartTriggerOccurred's own explicit confirm
            // this event would sit queued in TentativeState forever.
            callLog.Skip(raiseIndex + 1).Should()
                .Contain("Confirm", "TimerStartTriggerOccurred must be confirmed after it is raised, not just never");
        }

        // Regression test for #31 (findings B2/B3): HandleTerminated used to call
        // GetGrain<ITimerEventSchedulerGrain>(Host.CaseInstanceId, Host.InstanceId). That second
        // argument is NOT a key extension - ITimerEventSchedulerGrain is IGrainWithGuidKey, so the
        // 2-Guid-arg-looking call actually resolves to IGrainFactory.GetGrain<T>(Guid primaryKey,
        // string grainClassNamePrefix = null). Passing Host.InstanceId there doesn't scope
        // anything by plan item; it's just the wrong parameter, and on a real IGrainFactory it
        // fails to match TimerEventSchedulerGrain's type name and throws during grain-class
        // resolution. Here a loose mock only has GetGrain<ITimerEventSchedulerGrain>(caseInstanceId,
        // null) set up - the SAME (caseInstanceId, null) key ScheduleTimer's call site already used
        // correctly - so under the old buggy code this call site would miss the setup, resolve to
        // null, and CancelTimer would NullReferenceException. After the fix, both ScheduleTimer
        // (on Create) and CancelTimer (on Terminate) route through the identical
        // GrainFactory.GetScheduler(Host.CaseInstanceId) helper and hit the same mocked grain.
        [Fact]
        public async Task Trigger__Given_Available__When_Terminate__Then_CancelsCorrectlyKeyedSchedulerGrain()
        {
            var caseInstanceId = Guid.NewGuid();
            var instanceId = ShortGuid.NewGuid();

            var timerEventListener = new TimerEventListener
            {
                Id = "TimerA"
                // TimerExpression and TimerStart both left null: Create takes the "schedule
                // immediately" branch of HandleEnterAvailableFromCreate's switch directly, with no
                // IExpressionGrain evaluation needed first.
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

            var mockTimerSchedulerGrain = new Mock<ITimerEventSchedulerGrain>();
            mockTimerSchedulerGrain
                .Setup(x => x.ScheduleTimer(instanceId, It.IsAny<Iso8601>(), It.IsAny<DateTime?>(), It.IsAny<IDictionary<string, object>>()))
                .Returns(Task.CompletedTask);
            mockTimerSchedulerGrain
                .Setup(x => x.CancelTimer(instanceId))
                .Returns(Task.CompletedTask);

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory
                .Setup(x => x.GetGrain<ITimerEventSchedulerGrain>(caseInstanceId, null))
                .Returns(mockTimerSchedulerGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId).Returns(caseInstanceId);
            mockHost.Setup(x => x.InstanceId).Returns(instanceId);
            mockHost.Setup(x => x.Definition).Returns(pi);
            mockHost.Setup(x => x.State).Returns(testStore);
            mockHost.Setup(x => x.GrainFactory).Returns(mockGrainFactory.Object);

            var mockMachine = new MockPlanItemStateMachine(testStore);

            var subject = new TimerEventListenerBehavior(mockHost.Object, timerEventListener, mockMachine.Object);

            await subject.Trigger(PlanItemTransition.Create);
            mockMachine.Object.State.Should().Be(PlanItemState.Available);

            await subject.Trigger(PlanItemTransition.Terminate);

            mockMachine.Object.State.Should().Be(PlanItemState.Terminated);
            mockTimerSchedulerGrain.Verify(x => x.CancelTimer(instanceId), Times.Once,
                "HandleTerminated must resolve ITimerEventSchedulerGrain via the case instance id " +
                "key alone, matching the grain's actual IGrainWithGuidKey shape - not throw trying " +
                "to match a bogus grainClassNamePrefix built from the plan item's instance id");
        }
    }
}
