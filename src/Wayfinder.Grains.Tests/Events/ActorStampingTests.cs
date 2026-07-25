using System;
using Wayfinder.Grains.Events;
using Wayfinder.Grains.Interfaces;
using FluentAssertions;
using Xunit;

namespace Wayfinder.Grains.Tests.Events
{
    // ADO #59 - fast, isolated coverage of Events.ActorStamping (the helper CmmnElementGrain.
    // RaiseEvent and CaseDefinitionGrain.Define both call at append time), independent of the
    // TestCluster-based proof that it is actually wired into a real RaiseEvent path (see Wayfinder.
    // Grains.Tests.Integration.Events.ActorStampingIntegrationTests).
    public class ActorStampingTests
    {
        private class TestEvent : IActorStampedEvent
        {
            public Guid ActorPrincipalId { get; set; }
            public ActorPrincipalType ActorPrincipalType { get; set; }
            public string ActorOnBehalfOf { get; set; }
        }

        private class NotStampedEvent
        {
        }

        public ActorStampingTests()
        {
            // Each test sets its own CaseRequestContext values (or deliberately leaves them
            // unset) - AsyncLocal-backed, so this does not leak between tests (matches how every
            // other CaseRequestContext-driven test in this solution already relies on isolation
            // per test method).
        }

        [Fact]
        public void Apply__Given_UserPrincipal__Then_StampsFromCaseRequestContext()
        {
            var userId = Guid.NewGuid();
            CaseRequestContext.UserId = userId;
            CaseRequestContext.ActorPrincipalType = ActorPrincipalType.User;
            CaseRequestContext.ActorOnBehalfOf = null;

            var @event = new TestEvent();
            ActorStamping.Apply(@event);

            @event.ActorPrincipalId.Should().Be(userId);
            @event.ActorPrincipalType.Should().Be(ActorPrincipalType.User);
            @event.ActorOnBehalfOf.Should().BeNull();
        }

        [Fact]
        public void Apply__Given_ClientPrincipalOnBehalfOfUser__Then_StampsBothDistinctly()
        {
            var clientPrincipalId = Guid.NewGuid();
            CaseRequestContext.UserId = clientPrincipalId;
            CaseRequestContext.ActorPrincipalType = ActorPrincipalType.Client;
            CaseRequestContext.ActorOnBehalfOf = "end-user@example.com";

            var @event = new TestEvent();
            ActorStamping.Apply(@event);

            @event.ActorPrincipalId.Should().Be(clientPrincipalId);
            @event.ActorPrincipalType.Should().Be(ActorPrincipalType.Client);
            @event.ActorOnBehalfOf.Should().Be("end-user@example.com");
        }

        // ADO #59 - critical safety net: RaiseEvent is reached by flows with no ambient identity
        // at all (a timer tick's stream callback, a transition cascade re-entering a reactivated,
        // previously-idle grain) where CaseRequestContext.UserId throws (ADO #33 - by design, for
        // the request-facing surface). Stamping must never turn one of those pre-existing,
        // legitimate internal flows into a fault just because nobody was "logged in" for that
        // particular step.
        [Fact]
        public void Apply__Given_NoAmbientIdentity__Then_DefaultsActorPrincipalIdToEmptyWithoutThrowing()
        {
            // Deliberately does not set CaseRequestContext.UserId - simulates a RaiseEvent call
            // reached from a flow with no request/identity context at all.
            var @event = new TestEvent();

            var act = () => ActorStamping.Apply(@event);

            act.Should().NotThrow();
            @event.ActorPrincipalId.Should().Be(Guid.Empty);
            @event.ActorPrincipalType.Should().Be(ActorPrincipalType.User);
            @event.ActorOnBehalfOf.Should().BeNull();
        }

        [Fact]
        public void Apply__Given_EventNotImplementingIActorStampedEvent__Then_IsANoOp()
        {
            CaseRequestContext.UserId = Guid.NewGuid();

            var @event = new NotStampedEvent();

            var act = () => ActorStamping.Apply(@event);

            act.Should().NotThrow();
        }
    }
}
