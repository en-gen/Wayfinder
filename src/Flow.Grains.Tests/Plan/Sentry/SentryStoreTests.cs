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

        // D11 (generalizes PR !18's single-OnPart-only IfPartNotSatisfied) - 8.5: a sentry whose
        // OnParts have all occurred but whose IfPart is false must be able to re-evaluate on a
        // subsequent occurrence (see SentryGrain.HandleOnPartOccurred and this event's remarks).
        // Pins that OnPartNotRearmed clears only the ONE named OnPart - not every recorded OnPart -
        // so a sibling OnPart's occurrence survives.
        [Fact]
        public void Apply__Given_OnPartNotRearmed__Then_ClearsOnlyThatOnPart()
        {
            var rearmedOnPartId = ShortGuid.NewGuid();
            var siblingOnPartId = ShortGuid.NewGuid();

            var subject = new SentryStore();
            subject.Apply(new OnPartOccurred { OnPart = new PlanItemOnPart { Id = rearmedOnPartId } });
            subject.Apply(new OnPartOccurred { OnPart = new PlanItemOnPart { Id = siblingOnPartId } });

            subject.OccurredOnPartIds.Should().Contain(rearmedOnPartId.ToString(),
                "both OnParts must be recorded as occurred before this test proves only one gets cleared");
            subject.OccurredOnPartIds.Should().Contain(siblingOnPartId.ToString(),
                "both OnParts must be recorded as occurred before this test proves only one gets cleared");

            var @event = new OnPartNotRearmed { OnPartId = rearmedOnPartId };
            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

            subject.OccurredOnPartIds.Should().NotContain(rearmedOnPartId,
                "the named OnPart must be forgotten so a later, distinct occurrence of it re-triggers the AND-join");
            subject.OccurredOnPartIds.Should().Contain(siblingOnPartId,
                "a sibling OnPart's occurrence must survive - clearing it too would make an AND-join with a one-shot sibling source permanently unreachable");
        }

        // D5 - a repeatable PlanItem's entry criterion is backed by the SAME SentryGrain instance
        // across every repetition (see SentryStore's class remarks). Pins that a genuinely NEW
        // occurrence (a different OccurrenceToken - e.g. a different repetition's PlanItem grain
        // instance) of an OnPart that already occurred is NOT treated as redelivery, even though
        // the Sentry already reached Satisfied for the earlier occurrence.
        [Fact]
        public void IsRedelivery__Given_SatisfiedOnPartWithDifferentToken__Then_False()
        {
            var onPartId = ShortGuid.NewGuid();
            var firstOccurrenceToken = ShortGuid.NewGuid().ToString();
            var secondOccurrenceToken = ShortGuid.NewGuid().ToString();

            var subject = new SentryStore();
            subject.Apply(new OnPartOccurred { OnPart = new PlanItemOnPart { Id = onPartId }, OccurrenceToken = firstOccurrenceToken });
            subject.Apply(new Satisfied());

            subject.Satisfied.Should().BeTrue("the sentry must already be Satisfied by the first occurrence before this test proves a second, distinct occurrence is not redelivery");

            subject.IsRedelivery(onPartId, secondOccurrenceToken).Should().BeFalse(
                "a different OccurrenceToken (e.g. a different repetition's PlanItem grain instance) means this is a genuinely new occurrence, not a redelivery of the one that already satisfied the sentry");
        }

        // D5 - the inverse: the SAME OccurrenceToken recorded twice for an OnPart IS redelivery of
        // the exact same logical transition and must be recognized as such (this is what keeps
        // HandlePlanItemTransitioned__Given_SameEventDeliveredTwice__Then_
        // PublishSentrySatisfiedEventExactlyOnce passing).
        [Fact]
        public void IsRedelivery__Given_SameOccurrenceTokenTwice__Then_True()
        {
            var onPartId = ShortGuid.NewGuid();
            var occurrenceToken = ShortGuid.NewGuid().ToString();

            var subject = new SentryStore();
            subject.Apply(new OnPartOccurred { OnPart = new PlanItemOnPart { Id = onPartId }, OccurrenceToken = occurrenceToken });

            subject.IsRedelivery(onPartId, occurrenceToken).Should().BeTrue(
                "the identical OccurrenceToken means this is a redelivery of the same logical transition that already occurred");
        }

        // D5 - a null OccurrenceToken (CaseFileItemOnPart, which has no per-instance concept - see
        // class remarks) falls back to the original "any occurrence of this OnPart.Id counts as
        // redelivery" behavior, which D1's flagship CaseFileItem+IfPart test already depends on
        // being overridden only by an explicit OnPartNotRearmed clear, never by token comparison.
        [Fact]
        public void IsRedelivery__Given_NullOccurrenceToken__Then_TreatedAsRedeliveryOnceRecorded()
        {
            var onPartId = ShortGuid.NewGuid();

            var subject = new SentryStore();
            subject.Apply(new OnPartOccurred { OnPart = new CaseFileItemOnPart { Id = onPartId }, OccurrenceToken = null });

            subject.IsRedelivery(onPartId, null).Should().BeTrue(
                "with no distinguishing token available, a CaseFileItemOnPart occurrence latches until explicitly cleared by OnPartNotRearmed, not by token comparison");
        }

        // D10 - an IfPart evaluation fault is recorded distinctly from an evaluated-FALSE IfPart
        // (which leaves Satisfied false but Faulted also false) - see SentryGrain.HandleOnPartOccurred's
        // IfPartResult/EvaluateIfPart.
        [Fact]
        public void Apply__Given_Faulted__Then_UpdateState()
        {
            var @event = new Faulted();

            var subject = new SentryStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

            subject.Faulted.Should().BeTrue();
        }
    }
}
