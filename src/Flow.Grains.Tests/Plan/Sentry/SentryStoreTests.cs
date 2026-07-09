using Flow.Grains.Interfaces;
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
                .And.Be(@event.Updated);

            subject.OccurredOnPartIds.Should().Contain(@event.OnPart.Id);
        }

        // Pinning test for tracking OccurredOnParts by OnPart.Id instead of the OnPart object
        // itself. Note: CmmnElement (OnPart's base) already overrides Equals/GetHashCode by Id, so
        // this scenario did not actually double-count under the prior HashSet<OnPart> - verified
        // directly before making this change. Tracking by Id explicitly is kept anyway per the work
        // item, since it does not depend on the model type's Equals behaving correctly and avoids
        // persisting full OnPart object graphs just to record that an occurrence happened. This test
        // pins that two distinct OnPart instances sharing an Id are still counted once.
        [Fact]
        public void Apply__Given_OnPartOccurredTwiceWithDistinctInstancesSameId__Then_CountedOnce()
        {
            var onPartId = ShortGuid.NewGuid();

            var firstInstance = new PlanItemOnPart { Id = onPartId };
            var secondInstance = new PlanItemOnPart { Id = onPartId };

            firstInstance.Should().NotBeSameAs(secondInstance, "the two OnPart instances must be genuinely distinct objects");

            var subject = new SentryStore();

            subject.Apply(new OnPartOccurred { OnPart = firstInstance });
            subject.Apply(new OnPartOccurred { OnPart = secondInstance });

            subject.OccurredOnPartIds.Should().ContainSingle()
                .Which.Should().Be(onPartId);
        }

        [Fact]
        public void Apply__Given_Satisfied__Then_UpdateState()
        {
            var @event = new Satisfied();

            var subject = new SentryStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

            subject.Satisfied.Should().BeTrue();
        }

        // 8.5 - a sentry whose OnParts have all occurred but whose IfPart is false must be able to
        // re-evaluate on a subsequent occurrence (see SentryGrain.HandleOnPartOccurred and this
        // event's remarks). Pins that IfPartNotSatisfied forgets recorded occurrences rather than
        // only updating the timestamp - the mechanism this re-evaluation depends on.
        [Fact]
        public void Apply__Given_IfPartNotSatisfied__Then_ClearsOccurredOnPartIds()
        {
            var onPartId = ShortGuid.NewGuid();
            var subject = new SentryStore();
            subject.Apply(new OnPartOccurred { OnPart = new PlanItemOnPart { Id = onPartId } });

            subject.OccurredOnPartIds.Should().Contain(onPartId, "the OnPart must be recorded as occurred before this test proves it gets cleared");

            var @event = new IfPartNotSatisfied();
            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

            subject.OccurredOnPartIds.Should().BeEmpty();
            subject.Satisfied.Should().BeFalse("IfPartNotSatisfied means the sentry is explicitly NOT satisfied");
        }
    }
}
