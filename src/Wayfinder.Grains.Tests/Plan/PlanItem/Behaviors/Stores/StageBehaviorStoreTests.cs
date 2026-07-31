using System.Collections.Generic;
using System.Linq;
using AutoFixture.Xunit2;
using Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores;
using Wayfinder.Grains.Plan.PlanItem.Events;
using FluentAssertions;
using Xunit;

namespace Wayfinder.Grains.Tests.Plan.PlanItem.Behaviors.Stores
{
    public class StageBehaviorStoreTests
    {
        [Theory, AutoData]
        public void Apply__When_ChildCreated__Then_ChildIndexed
            (string planItemDefinitionId, string planItemInstanceId, int repetition)
        {
            var @event = new ChildCreated
            {
                PlanItemId = planItemDefinitionId,
                PlanItemInstanceId = planItemInstanceId,
                Repetition = repetition
            };

            var subject = new StageBehaviorStore();

            subject.Apply(@event);

            subject.Children.Should()
                .HaveCount(1)
                .And.ContainKey(planItemDefinitionId);

            subject.Children[planItemDefinitionId].Should()
                .HaveCount(1)
                .And.Contain(KeyValuePair.Create(planItemInstanceId, repetition));
        }

        // #161 - the redelivery guard's write side: recording a repetition request's source
        // instance id so a later delivery carrying the same id is recognized as a redelivery.
        [Theory, AutoData]
        public void Apply__When_ChildRepeated__Then_SourceInstanceIdRecorded(string sourceInstanceId)
        {
            var @event = new ChildRepeated { SourceInstanceId = sourceInstanceId };

            var subject = new StageBehaviorStore();

            subject.Apply(@event);

            subject.IsRepetitionRedelivery(sourceInstanceId).Should().BeTrue();
        }

        [Theory, AutoData]
        public void IsRepetitionRedelivery__Given_KnownId__Then_True(string sourceInstanceId)
        {
            var subject = new StageBehaviorStore();
            subject.Apply(new ChildRepeated { SourceInstanceId = sourceInstanceId });

            subject.IsRepetitionRedelivery(sourceInstanceId).Should().BeTrue();
        }

        [Theory, AutoData]
        public void IsRepetitionRedelivery__Given_UnknownId__Then_False(string knownId, string unknownId)
        {
            var subject = new StageBehaviorStore();
            subject.Apply(new ChildRepeated { SourceInstanceId = knownId });

            subject.IsRepetitionRedelivery(unknownId).Should().BeFalse();
        }

        // Pre-upgrade in-flight repetitions (a ChildRepeated raised by an older binary, before
        // SourceInstanceId existed) replay with a null id - see StageBehaviorStore's remarks. Null
        // must never match anything, not even another null, or every such legacy entry would
        // collapse into a single "already redelivered" sentinel and block unrelated repetitions.
        [Fact]
        public void IsRepetitionRedelivery__Given_NullSourceInstanceId__Then_False()
        {
            var subject = new StageBehaviorStore();
            subject.Apply(new ChildRepeated { SourceInstanceId = null });

            subject.IsRepetitionRedelivery(null).Should().BeFalse();
        }

        // #178 - the Suspended-buffer's write side: recording a pending repetition request so it
        // can be replayed once the container returns to Active.
        [Theory, AutoData]
        public void Apply__When_RepetitionBuffered__Then_PendingRepetitionRecorded(
            string sourceInstanceId, string planItemDefinitionId, int nextRepetition)
        {
            var @event = new RepetitionBuffered
            {
                SourceInstanceId = sourceInstanceId,
                PlanItemDefinitionId = planItemDefinitionId,
                NextRepetition = nextRepetition
            };

            var subject = new StageBehaviorStore();

            subject.Apply(@event);

            subject.PendingRepetitions.Should().HaveCount(1);
            subject.PendingRepetitions.Single().SourceInstanceId.Should().Be(sourceInstanceId);
            subject.PendingRepetitions.Single().PlanItemDefinitionId.Should().Be(planItemDefinitionId);
            subject.PendingRepetitions.Single().NextRepetition.Should().Be(nextRepetition);
            subject.HasPendingRepetition(sourceInstanceId).Should().BeTrue();
        }

        // #178 hazard 1 - a redelivered buffering request (same SourceInstanceId) must not queue a
        // second, duplicate pending entry.
        [Theory, AutoData]
        public void Apply__Given_RepetitionBufferedTwiceForSameSourceInstanceId__Then_OnlyOnePendingRepetitionRecorded(
            string sourceInstanceId, string planItemDefinitionId, int nextRepetition)
        {
            var subject = new StageBehaviorStore();

            subject.Apply(new RepetitionBuffered
            {
                SourceInstanceId = sourceInstanceId,
                PlanItemDefinitionId = planItemDefinitionId,
                NextRepetition = nextRepetition
            });
            subject.Apply(new RepetitionBuffered
            {
                SourceInstanceId = sourceInstanceId,
                PlanItemDefinitionId = planItemDefinitionId,
                NextRepetition = nextRepetition
            });

            subject.PendingRepetitions.Should().HaveCount(1,
                "a redelivered PlanItemRepetitionCriteriaMetEvent while still Suspended must not double-buffer");
        }

        [Theory, AutoData]
        public void HasPendingRepetition__Given_UnknownId__Then_False(string knownId, string unknownId, string planItemDefinitionId, int nextRepetition)
        {
            var subject = new StageBehaviorStore();
            subject.Apply(new RepetitionBuffered { SourceInstanceId = knownId, PlanItemDefinitionId = planItemDefinitionId, NextRepetition = nextRepetition });

            subject.HasPendingRepetition(unknownId).Should().BeFalse();
        }

        // #178 - the buffer's drain side: removing an entry once it has been replayed (whether
        // spawned or refused by the #67 ceiling - see StageBehavior.DrainPendingRepetitions).
        [Theory, AutoData]
        public void Apply__When_RepetitionBufferDrained__Then_PendingRepetitionRemoved(
            string sourceInstanceId, string planItemDefinitionId, int nextRepetition)
        {
            var subject = new StageBehaviorStore();
            subject.Apply(new RepetitionBuffered
            {
                SourceInstanceId = sourceInstanceId,
                PlanItemDefinitionId = planItemDefinitionId,
                NextRepetition = nextRepetition
            });

            subject.Apply(new RepetitionBufferDrained { SourceInstanceId = sourceInstanceId });

            subject.PendingRepetitions.Should().BeEmpty();
            subject.HasPendingRepetition(sourceInstanceId).Should().BeFalse();
        }

        [Theory, AutoData]
        public void Apply__Given_RepetitionBufferDrainedForUnknownId__Then_NoOp(string unknownId)
        {
            var subject = new StageBehaviorStore();

            subject.Apply(new RepetitionBufferDrained { SourceInstanceId = unknownId });

            subject.PendingRepetitions.Should().BeEmpty();
        }

        // #198 - the commutative outstanding/settled verdict pair (StageBehaviorStore's own
        // remarks have the full rationale). The ORDINARY order: a terminal child's
        // PlanItemTransitionedEvent{WillRepeat=true} marks pending BEFORE the matching
        // PlanItemRepetitionCriteriaMetEvent resolves it.
        [Theory, AutoData]
        public void Apply__RepetitionPending_ThenRepetitionResolved__Then_ClearsOutstandingAndSettles(string sourceInstanceId)
        {
            var subject = new StageBehaviorStore();

            subject.Apply(new RepetitionPending { SourceInstanceId = sourceInstanceId });
            subject.HasOutstandingRepetitionVerdict(sourceInstanceId).Should().BeTrue(
                "a terminal child that will repeat must defer Table 8.12 completion until its request resolves");
            subject.AnyOutstandingRepetitionVerdicts.Should().BeTrue();

            subject.Apply(new RepetitionResolved { SourceInstanceId = sourceInstanceId });

            subject.HasOutstandingRepetitionVerdict(sourceInstanceId).Should().BeFalse();
            subject.AnyOutstandingRepetitionVerdicts.Should().BeFalse();
            subject.IsRepetitionVerdictSettled(sourceInstanceId).Should().BeTrue();
        }

        // #198 (BLOCKER regression, review round 2) - the REORDERED arrival: on develop,
        // PlanItemTransitionedEvent and PlanItemRepetitionCriteriaMetEvent travel on separate,
        // unordered streams (AddMemoryStreams hash-maps the two namespaces onto different queues
        // with independent pulling agents), and the repetition-met event routinely arrives and
        // resolves FIRST - measured at a majority (~60-75%) of runs, not an edge case. The
        // original single-set design ("add on pending, remove on resolve") was not commutative
        // under this ordering: Apply(RepetitionResolved) running before the matching
        // Apply(RepetitionPending) would leave a mark added with NOTHING left to ever remove it -
        // AnyOutstandingRepetitionVerdicts stuck true, Table 8.12 permanently disabled for that
        // container. This is the single test that would have caught it: resolve BEFORE marking,
        // and assert the mark is correctly a no-op instead of stranding.
        [Theory, AutoData]
        public void Apply__RepetitionResolved_ThenRepetitionPending__Then_DoesNotStrandTheMark(string sourceInstanceId)
        {
            var subject = new StageBehaviorStore();

            // the repetition-met event arrives and resolves FIRST - nothing was ever marked pending
            subject.Apply(new RepetitionResolved { SourceInstanceId = sourceInstanceId });
            subject.IsRepetitionVerdictSettled(sourceInstanceId).Should().BeTrue();
            subject.AnyOutstandingRepetitionVerdicts.Should().BeFalse();

            // the terminal transition event's WillRepeat=true mark arrives SECOND, late
            subject.Apply(new RepetitionPending { SourceInstanceId = sourceInstanceId });

            subject.HasOutstandingRepetitionVerdict(sourceInstanceId).Should().BeFalse(
                "a mark arriving AFTER its own resolution has nothing left to resolve it - adding it " +
                "would strand Table 8.12 for this container forever (#198 review round 2 blocker)");
            subject.AnyOutstandingRepetitionVerdicts.Should().BeFalse(
                "the reordered arrival must converge to the SAME outcome as the ordinary order - " +
                "never permanently outstanding");
        }

        // #198 (review round 2) - the inverted MarkRepetitionPending idempotency guard this same
        // fix closes: an at-least-once REDELIVERY of the terminal PlanItemTransitionedEvent
        // arriving AFTER its own resolution (whichever order the FIRST delivery and the
        // resolution happened in) must also not re-add a mark with nothing left to resolve it.
        [Theory, AutoData]
        public void Apply__RepetitionPendingRedeliveredAfterSettlement__Then_DoesNotReAddToOutstanding(string sourceInstanceId)
        {
            var subject = new StageBehaviorStore();

            subject.Apply(new RepetitionPending { SourceInstanceId = sourceInstanceId });
            subject.Apply(new RepetitionResolved { SourceInstanceId = sourceInstanceId });
            subject.AnyOutstandingRepetitionVerdicts.Should().BeFalse();

            // a stream redelivery of the SAME PlanItemTransitionedEvent, arriving after settlement
            subject.Apply(new RepetitionPending { SourceInstanceId = sourceInstanceId });

            subject.HasOutstandingRepetitionVerdict(sourceInstanceId).Should().BeFalse(
                "a redelivered mark for an already-settled source instance must stay a no-op");
            subject.AnyOutstandingRepetitionVerdicts.Should().BeFalse();
        }

        // Table 8.9's <impossible> cell requires ALL outstanding verdicts to clear, not just one -
        // two repeating children can race independently, and completion must wait for both.
        [Theory, AutoData]
        public void AnyOutstandingRepetitionVerdicts__Given_TwoOutstandingMarks__Then_TrueUntilBothResolve(
            string sourceInstanceIdA, string sourceInstanceIdB)
        {
            var subject = new StageBehaviorStore();

            subject.Apply(new RepetitionPending { SourceInstanceId = sourceInstanceIdA });
            subject.Apply(new RepetitionPending { SourceInstanceId = sourceInstanceIdB });
            subject.AnyOutstandingRepetitionVerdicts.Should().BeTrue();

            subject.Apply(new RepetitionResolved { SourceInstanceId = sourceInstanceIdA });
            subject.AnyOutstandingRepetitionVerdicts.Should().BeTrue(
                "B is still outstanding - completion must not proceed while EITHER child's verdict " +
                "is unresolved");

            subject.Apply(new RepetitionResolved { SourceInstanceId = sourceInstanceIdB });
            subject.AnyOutstandingRepetitionVerdicts.Should().BeFalse();
        }

        // #198 (should-fix, review round 2) - StageBehavior.ClearOutstandingRepetitionVerdictsOnTerminalEntry's
        // write side: a blanket clear on entry to Completed/Terminated/Failed, closing both a
        // dead-state leak once nothing will ever consult the outstanding set again, and a
        // permanent Table-8.12-completion-blocking strand when DrainPendingRepetitions hits the
        // #67 ceiling mid-batch (drain is never auto-retried on Reactivate).
        [Theory, AutoData]
        public void Apply__When_OutstandingRepetitionVerdictsCleared__Then_OutstandingClearedButNotSettled(string sourceInstanceId)
        {
            var subject = new StageBehaviorStore();
            subject.Apply(new RepetitionPending { SourceInstanceId = sourceInstanceId });

            subject.Apply(new OutstandingRepetitionVerdictsCleared());

            subject.AnyOutstandingRepetitionVerdicts.Should().BeFalse();
            // deliberately NOT settled: a still-in-flight, unrelated resolution for this id must
            // still be free to settle it normally afterward (Apply(RepetitionResolved) is
            // idempotent either way) - this clear only drops the OUTSTANDING mark, it does not
            // manufacture a tombstone.
            subject.IsRepetitionVerdictSettled(sourceInstanceId).Should().BeFalse();
        }
    }
}
