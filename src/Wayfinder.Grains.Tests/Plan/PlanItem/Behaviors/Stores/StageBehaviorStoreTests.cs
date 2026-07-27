using System.Collections.Generic;
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
    }
}
