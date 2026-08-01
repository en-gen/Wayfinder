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

        // #198 - the settled-request record, one scenario per branch of StageBehavior's
        // repetition handling that can resolve a request. The blocking predicate
        // (StageBehavior.RepetitionRequestsAwaitingResolution) reads exactly this, so a branch
        // missing from here is a branch that would wedge Table 8.12 completion forever.

        // SPAWNED - the successor exists; the #161 event does double duty and no separate settle
        // event is raised on that path.
        [Theory, AutoData]
        public void Apply__When_ChildRepeated__Then_RequestAlsoSettled(string sourceInstanceId)
        {
            var subject = new StageBehaviorStore();

            subject.Apply(new ChildRepeated { SourceInstanceId = sourceInstanceId });

            subject.IsRepetitionSettled(sourceInstanceId).Should().BeTrue();
        }

        // REFUSED - the #67 ceiling, #178's terminal/Failed containers, an unrecognized child, a
        // buffered entry drained against a definition that no longer exists, and an entry stranded
        // by a mid-drain ceiling fault all journal the same event; only the Reason differs. The
        // positive case is asserted by …Then_NotTreatedAsRedelivery below, which pins the same
        // IsRepetitionSettled == true AND the property that distinguishes this set from the #161
        // guard.

        // SUSPENDED-BUFFERED - deliberately NOT settled: #178 buffers the request for replay on
        // resume, so it is genuinely still pending and blocking completion is the correct answer.
        [Theory, AutoData]
        public void Apply__When_RepetitionBuffered__Then_RequestIsNotYetSettled(
            string sourceInstanceId, string planItemDefinitionId, int nextRepetition)
        {
            var subject = new StageBehaviorStore();

            subject.Apply(new RepetitionBuffered
            {
                SourceInstanceId = sourceInstanceId,
                PlanItemDefinitionId = planItemDefinitionId,
                NextRepetition = nextRepetition
            });

            subject.IsRepetitionSettled(sourceInstanceId).Should().BeFalse(
                "a buffered request is still owed a successor once this container resumes (#178)");
        }

        // ...and draining alone does not settle it either - what settles it is whatever the drain
        // DID (spawn -> ChildRepeated, ceiling/unknown-child -> RepetitionRequestSettled).
        // RepetitionBufferDrained is only the queue bookkeeping.
        [Theory, AutoData]
        public void Apply__When_BufferedRequestDrainedAndSpawned__Then_RequestSettled(
            string sourceInstanceId, string planItemDefinitionId, int nextRepetition)
        {
            var subject = new StageBehaviorStore();
            subject.Apply(new RepetitionBuffered
            {
                SourceInstanceId = sourceInstanceId,
                PlanItemDefinitionId = planItemDefinitionId,
                NextRepetition = nextRepetition
            });

            subject.Apply(new ChildRepeated { SourceInstanceId = sourceInstanceId });
            subject.Apply(new RepetitionBufferDrained { SourceInstanceId = sourceInstanceId });

            subject.PendingRepetitions.Should().BeEmpty();
            subject.IsRepetitionSettled(sourceInstanceId).Should().BeTrue();
        }

        [Theory, AutoData]
        public void IsRepetitionSettled__Given_UnknownId__Then_False(string knownId, string unknownId)
        {
            var subject = new StageBehaviorStore();
            subject.Apply(new ChildRepeated { SourceInstanceId = knownId });

            subject.IsRepetitionSettled(unknownId).Should().BeFalse();
        }

        // Same null discipline as IsRepetitionRedelivery above, and for the same reason: a
        // null-id legacy entry must not collapse into a sentinel that settles every unrelated
        // request.
        [Fact]
        public void IsRepetitionSettled__Given_NullSourceInstanceId__Then_False()
        {
            var subject = new StageBehaviorStore();
            subject.Apply(new RepetitionRequestSettled { SourceInstanceId = null, Reason = "legacy" });

            subject.IsRepetitionSettled(null).Should().BeFalse();
        }

        // The two sets are NOT aliases: a definitively-refused request is settled (stop blocking
        // completion) but was never spawned, so a redelivery of it must still reach
        // HandleChildRepeated's refusal branches rather than being swallowed by the #161 guard.
        [Theory, AutoData]
        public void Apply__When_RepetitionRequestSettled__Then_NotTreatedAsRedelivery(string sourceInstanceId)
        {
            var subject = new StageBehaviorStore();

            subject.Apply(new RepetitionRequestSettled { SourceInstanceId = sourceInstanceId, Reason = "ceiling" });

            subject.IsRepetitionSettled(sourceInstanceId).Should().BeTrue();
            subject.IsRepetitionRedelivery(sourceInstanceId).Should().BeFalse(
                "#161's guard means 'a child was already spawned', which a refusal never did");
        }
    }
}
