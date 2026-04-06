using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.Sentry;
using Flow.Grains.Plan.Sentry.Events;
using FluentAssertions;
using FluentAssertions.Common;
using Xunit;

namespace Flow.Grains.Tests.Plan.Sentry
{
    public class SentryStoreTests
    {
        [Fact]
        public void Apply__Given_OnPartOccurred__Then_UpdateState()
        {
            var @event = new OnPartOccurred
            {
                OnPart = new PlanItemOnPart()
            };

            var subject = new SentryStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);

            subject.OccurredOnParts.Should().Contain(@event.OnPart);
        }

        [Fact]
        public void Apply__Given_Satisfied__Then_UpdateState()
        {
            var @event = new Satisfied();

            var subject = new SentryStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);

            subject.Satisfied.Should().BeTrue();
        }
    }
}
