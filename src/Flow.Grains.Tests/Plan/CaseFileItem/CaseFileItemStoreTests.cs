using System.Text.Json.Nodes;
using Flow.Grains.Interfaces.Plan.CaseFileItem;
using Flow.Grains.Plan.CaseFileItem;
using Flow.Grains.Plan.CaseFileItem.Events;
using FluentAssertions;
using FluentAssertions.Common;
using Xunit;

namespace Flow.Grains.Tests.Plan.CaseFileItem
{
    public class CaseFileItemStoreTests
    {
        // 8.3 - CaseFileItem Lifecycle
        // ~~~~~
        // create transitions Ø -> Available; CaseFileItemStore.CaseFileItemState defaults to
        // Available for exactly that reason - see CaseFileItemStore's field comment.
        [Fact]
        public void CaseFileItemState__Given_NewStore__Then_DefaultsToAvailable()
        {
            var subject = new CaseFileItemStore();

            subject.CaseFileItemState.Should().Be(CaseFileItemState.Available);
        }

        [Fact]
        public void Apply__Given_ValueChanged__Then_UpdateState()
        {
            var value = JsonValue.Create("hello");
            var @event = new ValueChanged
            {
                Value = value
            };

            var subject = new CaseFileItemStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

            subject.Value.Should().BeSameAs(value);
        }

        // Table 8.2: update/replace are both modeled as ValueChanged at the store layer (the
        // distinct standardEvent each publishes is what differs - see CaseFileItemGrain); a second
        // ValueChanged (representing either operation) must simply overwrite the prior Value.
        [Fact]
        public void Apply__Given_ValueChangedTwice__Then_LatestValueWins()
        {
            var subject = new CaseFileItemStore();

            subject.Apply(new ValueChanged { Value = JsonValue.Create("first") });
            subject.Apply(new ValueChanged { Value = JsonValue.Create("second") });

            subject.Value.ToString().Should().Be("second");
        }

        [Fact]
        public void Apply__Given_ChildAdded__Then_UpdateState()
        {
            var @event = new ChildAdded
            {
                ChildCaseFileItemId = "child-1"
            };

            var subject = new CaseFileItemStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

            subject.ChildCaseFileItemIds.Should().Contain("child-1");
        }

        [Fact]
        public void Apply__Given_ChildAddedThenChildRemoved__Then_ChildNoLongerTracked()
        {
            var subject = new CaseFileItemStore();

            subject.Apply(new ChildAdded { ChildCaseFileItemId = "child-1" });
            subject.Apply(new ChildRemoved { ChildCaseFileItemId = "child-1" });

            subject.ChildCaseFileItemIds.Should().NotContain("child-1");
        }

        [Fact]
        public void Apply__Given_ReferenceAdded__Then_UpdateState()
        {
            var @event = new ReferenceAdded
            {
                TargetCaseFileItemId = "target-1"
            };

            var subject = new CaseFileItemStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

            subject.TargetCaseFileItemIds.Should().Contain("target-1");
        }

        [Fact]
        public void Apply__Given_ReferenceAddedThenReferenceRemoved__Then_ReferenceNoLongerTracked()
        {
            var subject = new CaseFileItemStore();

            subject.Apply(new ReferenceAdded { TargetCaseFileItemId = "target-1" });
            subject.Apply(new ReferenceRemoved { TargetCaseFileItemId = "target-1" });

            subject.TargetCaseFileItemIds.Should().NotContain("target-1");
        }

        // Table 8.2: delete (Available -> Discarded). Terminal state.
        [Fact]
        public void Apply__Given_Discarded__Then_UpdateState()
        {
            var @event = new Discarded();

            var subject = new CaseFileItemStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

            subject.CaseFileItemState.Should().Be(CaseFileItemState.Discarded);
        }
    }
}
