using System;
using System.Reflection;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Wayfinder.Grains.Plan.Role;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Moq;
using Orleans;
using Orleans.Streams;
using Xunit;

namespace Wayfinder.Grains.Tests.Plan.PlanItem.Behaviors
{
    public class UserEventListenerBehaviorTests
    {
        [Fact]
        public async Task HandleTransitioned__Given_ConfiguredBehavior__When_StateMachineCannotComplete__Then_ReturnFalse()
        {
            var eventListener = new UserEventListener();

            var mockStore = new Mock<IBehaviorStore>();
            mockStore.Setup(x => x.PlanItemDefinition)
                .Returns(eventListener);
            mockStore.Setup(x => x.PlanItemState)
                .Returns(PlanItemState.Suspended);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.State)
                .Returns(mockStore.Object);

            var mockMachine = new MockPlanItemStateMachine(mockStore.Object);

            var subject = new UserEventListenerBehavior(mockHost.Object, eventListener, mockMachine.Object);

            await (Task)typeof(UserEventListenerBehavior)
                .GetMethod("HandleTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    // the transition itself is irrelevant.  current state that would be the result
                    // of this transition is what's relevant
                    (Stateless.StateMachine<PlanItemState, PlanItemTransition>.Transition)null
                });

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Never);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Never);
        }

        [Fact]
        public async Task HandleTransitioned__Given_ConfiguredBehavior__When_StateMachineCannotCompleteCausesStateChange__Then_RaiseEvent()
        {
            var eventListener = new UserEventListener();

            var mockStore = new Mock<IBehaviorStore>();
            mockStore.Setup(x => x.PlanItemDefinition)
                .Returns(eventListener);
            mockStore.Setup(x => x.PlanItemState)
                .Returns(PlanItemState.Suspended);
            mockStore.Setup(x => x.UserCompletable)
                .Returns(true);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.State)
                .Returns(mockStore.Object);

            UserCompletableCriteriaMet capturedEvent = null;
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()))
                .Callback<UserCompletableCriteriaMet>(x => capturedEvent = x);

            var mockMachine = new MockPlanItemStateMachine(mockStore.Object);

            var subject = new UserEventListenerBehavior(mockHost.Object, eventListener, mockMachine.Object);

            await (Task)typeof(UserEventListenerBehavior)
                .GetMethod("HandleTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    // the transition itself is irrelevant.  current state that would be the result
                    // of this transition is what's relevant
                    (Stateless.StateMachine<PlanItemState, PlanItemTransition>.Transition)null
                });

            capturedEvent.Should().NotBeNull();
            capturedEvent.UserCompletable.Should().BeFalse();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once);
        }

        [Fact]
        public async Task HandleTransitioned__Given_ConfiguredBehaviorWithoutRoleRefs__When_StateMachineCanCompleteCausesStateChange__Then_RaiseEvent()
        {
            var eventListener = new UserEventListener();

            var mockStore = new Mock<IBehaviorStore>();
            mockStore.Setup(x => x.PlanItemDefinition)
                .Returns(eventListener);
            mockStore.Setup(x => x.PlanItemState)
                .Returns(PlanItemState.Available);
            mockStore.Setup(x => x.UserCompletable)
                .Returns(false);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.State)
                .Returns(mockStore.Object);

            UserCompletableCriteriaMet capturedEvent = null;
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()))
                .Callback<UserCompletableCriteriaMet>(x => capturedEvent = x);

            var mockMachine = new MockPlanItemStateMachine(mockStore.Object);

            var subject = new UserEventListenerBehavior(mockHost.Object, eventListener, mockMachine.Object);

            await (Task)typeof(UserEventListenerBehavior)
                .GetMethod("HandleTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    // the transition itself is irrelevant.  current state that would be the result
                    // of this transition is what's relevant
                    (Stateless.StateMachine<PlanItemState, PlanItemTransition>.Transition)null
                });

            capturedEvent.Should().NotBeNull();
            capturedEvent.UserCompletable.Should().BeTrue();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once);
        }

        [Fact]
        public async Task HandleTransitioned__Given_ConfiguredBehaviorWithRoleRefs__When_UserNotAuthorized__Then_False()
        {
            var caseInstanceId = Guid.NewGuid();

            var roleId = "admin";
            var eventListener = new UserEventListener
            {
                AuthorizedRoleRefs = new[] { roleId }
            };

            var mockStore = new Mock<IBehaviorStore>();
            mockStore.Setup(x => x.PlanItemDefinition)
                .Returns(eventListener);
            mockStore.Setup(x => x.PlanItemState)
                .Returns(PlanItemState.Available);
            mockStore.Setup(x => x.UserCompletable)
                .Returns(true);

            var mockRoleGrain = new Mock<IRoleGrain>();
            mockRoleGrain.Setup(x => x.Authorize(It.IsAny<GrainCancellationToken>()))
                .ReturnsAsync(false);

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IRoleGrain>(caseInstanceId, roleId, null))
                .Returns(mockRoleGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.State)
                .Returns(mockStore.Object);
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            UserCompletableCriteriaMet capturedEvent = null;
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()))
                .Callback<UserCompletableCriteriaMet>(x => capturedEvent = x);

            var mockMachine = new MockPlanItemStateMachine(mockStore.Object);

            var subject = new UserEventListenerBehavior(mockHost.Object, eventListener, mockMachine.Object);

            await (Task)typeof(UserEventListenerBehavior)
                .GetMethod("HandleTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    // the transition itself is irrelevant.  current state that would be the result
                    // of this transition is what's relevant
                    (Stateless.StateMachine<PlanItemState, PlanItemTransition>.Transition)null
                });

            capturedEvent.Should().NotBeNull();
            capturedEvent.UserCompletable.Should().BeFalse();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once);
        }

        [Fact]
        public async Task HandleTransitioned__Given_ConfiguredBehaviorWithRoleRefs__When_UserAuthorized__Then_False()
        {
            var caseInstanceId = Guid.NewGuid();

            var adminRole = "admin";
            var userRole = "user";
            var eventListener = new UserEventListener
            {
                AuthorizedRoleRefs = new[] { adminRole, userRole }
            };

            var mockStore = new Mock<IBehaviorStore>();
            mockStore.Setup(x => x.PlanItemDefinition)
                .Returns(eventListener);
            mockStore.Setup(x => x.PlanItemState)
                .Returns(PlanItemState.Available);
            mockStore.Setup(x => x.UserCompletable)
                .Returns(false);

            var mockAdminRoleGrain = new Mock<IRoleGrain>();
            mockAdminRoleGrain.Setup(x => x.Authorize(It.IsAny<GrainCancellationToken>()))
                .ReturnsAsync(false);
            var mockUserRoleGrain = new Mock<IRoleGrain>();
            mockAdminRoleGrain.Setup(x => x.Authorize(It.IsAny<GrainCancellationToken>()))
                .ReturnsAsync(true);

            var mockGrainFactory = new Mock<IGrainFactory>();
            mockGrainFactory.Setup(x => x.GetGrain<IRoleGrain>(caseInstanceId, adminRole, null))
                .Returns(mockAdminRoleGrain.Object);
            mockGrainFactory.Setup(x => x.GetGrain<IRoleGrain>(caseInstanceId, userRole, null))
                .Returns(mockUserRoleGrain.Object);

            var mockHost = new Mock<IBehaviorHost>();
            mockHost.Setup(x => x.CaseInstanceId)
                .Returns(caseInstanceId);
            mockHost.Setup(x => x.State)
                .Returns(mockStore.Object);
            mockHost.Setup(x => x.GrainFactory)
                .Returns(mockGrainFactory.Object);

            UserCompletableCriteriaMet capturedEvent = null;
            mockHost.Setup(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()))
                .Callback<UserCompletableCriteriaMet>(x => capturedEvent = x);

            var mockMachine = new MockPlanItemStateMachine(mockStore.Object);

            var subject = new UserEventListenerBehavior(mockHost.Object, eventListener, mockMachine.Object);

            await (Task)typeof(UserEventListenerBehavior)
                .GetMethod("HandleTransitioned", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(subject, new object[]
                {
                    // the transition itself is irrelevant.  current state that would be the result
                    // of this transition is what's relevant
                    (Stateless.StateMachine<PlanItemState, PlanItemTransition>.Transition)null
                });

            capturedEvent.Should().NotBeNull();
            capturedEvent.UserCompletable.Should().BeTrue();

            mockHost.Verify(x => x.RaiseEvent(It.IsAny<UserCompletableCriteriaMet>()), Times.Once);
            mockHost.Verify(x => x.ConfirmEvents(), Times.Once);
        }
    }
}
