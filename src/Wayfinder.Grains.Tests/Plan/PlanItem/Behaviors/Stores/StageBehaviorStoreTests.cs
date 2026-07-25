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
    }
}
