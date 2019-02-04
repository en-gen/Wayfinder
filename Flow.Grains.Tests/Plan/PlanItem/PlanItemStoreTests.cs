using System;
using AutoFixture.Xunit2;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors.Stores;
using Flow.Grains.Plan.PlanItem.Events;
using FluentAssertions;
using FluentAssertions.Common;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem
{
    public class PlanItemStoreTests
    {
        [Fact]
        public void Ctor__Then_InitialState()
        {
            var subject = new PlanItemStore();

            subject.Created.Should().NotHaveValue();
            subject.Updated.Should().NotHaveValue();

            subject.CaseDefinitionId.Should().BeEmpty();
            subject.Definition.Should().BeNull();

            subject.PlanItemDefinition.Should().BeNull();
            subject.Required.Should().BeFalse();
            subject.PlanItemState.Should().Be(PlanItemState.Uninitialized);
            subject.ParentSuspendState.Should().BeNull();
            subject.EntryCriterionStore.Should().NotBeNull();
            subject.ExitCriterionStore.Should().NotBeNull();

            subject.EntryCriterionStore.SatisfiedByAddress.Should().BeNull();
            subject.EntryCriterionStore.State.Should().HaveFlag(CriterionState.Unsatisfied);
            subject.EntryCriterionStore.State.Should().NotHaveFlag(CriterionState.Satisfied);
            subject.EntryCriterionStore.State.Should().NotHaveFlag(CriterionState.OnPartOccurred);
            
            subject.ExitCriterionStore.SatisfiedByAddress.Should().BeNull();
            subject.ExitCriterionStore.State.Should().HaveFlag(CriterionState.Unsatisfied);
            subject.ExitCriterionStore.State.Should().NotHaveFlag(CriterionState.Satisfied);
            subject.ExitCriterionStore.State.Should().NotHaveFlag(CriterionState.OnPartOccurred);
        }

        [Fact]
        public void Apply__Given_Defined__Then_UpdateState()
        {
            var caseDefId = Guid.NewGuid();
            var planItem = new Interfaces.Model.PlanItem();
            var planItemDefinition = new PlanItemDefinition();

            var @event = new Defined
            {
                CaseDefinitionId = caseDefId,
                Definition = planItem,
                PlanItemDefinition = planItemDefinition
            };

            var subject = new PlanItemStore();

            subject.Apply(@event);

            subject.Created.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Created);

            subject.Updated.Should().BeNull();

            subject.Defined.Should().BeTrue();

            subject.CaseDefinitionId.Should().NotBeEmpty()
                .And.IsSameOrEqualTo(caseDefId);

            subject.Definition.Should().NotBeNull()
                .And.IsSameOrEqualTo(planItem);

            subject.PlanItemDefinition.Should().NotBeNull()
                .And.IsSameOrEqualTo(planItemDefinition);

            subject.PlanItemState.Should().Be(PlanItemState.Uninitialized);
        }

        [Fact]
        public void Apply__Given_Transitioned__Then_UpdateState()
        {
            var @event = new Transitioned
            {
                Source = PlanItemState.Uninitialized,
                Trigger = PlanItemTransition.Create,
                Destination = PlanItemState.Available
            };

            var subject = new PlanItemStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);

            subject.PlanItemState.Should().Be(@event.Destination);

            subject.ParentSuspendState.Should().BeNull();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Apply__Given_EntryCriterionSatisfied__When_OnPart__Then_UpdateState(bool onPartOccurred)
        {
            var scope = Guid.NewGuid().ToString();
            var sentryId = Guid.NewGuid().ToString();

            var @event = new EntryCriterionSatisfied
            {
                SourceScope = scope,
                SourceId = sentryId,
                OnPartOccurred = onPartOccurred
            };

            var subject = new PlanItemStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);
            
            subject.EntryCriterionStore.State.Should().HaveFlag(CriterionState.Satisfied);
            if (onPartOccurred)
            {
                subject.EntryCriterionStore.State.Should().HaveFlag(CriterionState.OnPartOccurred);
            }
            else
            {
                subject.EntryCriterionStore.State.Should().NotHaveFlag(CriterionState.OnPartOccurred);
            }

            subject.EntryCriterionStore.SatisfiedByAddress.Should().Be($"{scope}.{sentryId}");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Apply__Given_ExitCriterionSatisfied__When_OnPart__Then_UpdateState(bool onPartOccurred)
        {
            var scope = Guid.NewGuid().ToString();
            var sentryId = Guid.NewGuid().ToString();
            
            var @event = new ExitCriterionSatisfied
            {
                SourceScope = scope,
                SourceId = sentryId,
                OnPartOccurred = onPartOccurred
            };

            var subject = new PlanItemStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);

            subject.ExitCriterionStore.State.Should().HaveFlag(CriterionState.Satisfied);
            if (onPartOccurred)
            {
                subject.ExitCriterionStore.State.Should().HaveFlag(CriterionState.OnPartOccurred);
            }
            else
            {
                subject.ExitCriterionStore.State.Should().NotHaveFlag(CriterionState.OnPartOccurred);
            }

            subject.ExitCriterionStore.SatisfiedByAddress.Should().Be($"{scope}.{sentryId}");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Apply__Given_RequiredRuleEvaluated__Then_RequiredEqualsResult(bool ruleResult)
        {
            var subject = new PlanItemStore();

            var @event = new RequiredRuleEvaluated
            {
                Result = ruleResult
            };

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);

            subject.Required.Should().Be(ruleResult);
        }

        [Fact]
        public void Apply__Given_ManualActivationRuleEvaluated__Then_Updated()
        {
            var subject = new PlanItemStore();

            var @event = new ManualActivationRuleEvaluated();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);
        }

        [Fact]
        public void Apply__Given_RepetitionRuleEvaluated__Then_Updated()
        {
            var subject = new PlanItemStore();

            var @event = new RepetitionRuleEvaluated();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);
        }

        [Fact]
        public void Apply__Given_ParentSuspended__Then_ParentSuspendedStateSet()
        {
            var preSuspendedState = PlanItemState.Enabled;

            var subject = new PlanItemStore();
            subject.Apply(new Transitioned
            {
                Source = PlanItemState.Available,
                Trigger = PlanItemTransition.Enable,
                Destination = preSuspendedState
            });

            var @event = new ParentSuspended();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);

            subject.ParentSuspendState.Should().Be(preSuspendedState);
        }

        [Fact]
        public void Apply__Given_ParentResumed__Then_ParentSuspendedStateNull()
        {
            var preSuspendedState = PlanItemState.Enabled;

            var subject = new PlanItemStore();
            subject.Apply(new Transitioned
            {
                Source = PlanItemState.Available,
                Trigger = PlanItemTransition.Enable,
                Destination = preSuspendedState
            });
            subject.Apply(new ParentSuspended());

            subject.ParentSuspendState.Should().NotBeNull();

            var @event = new ParentResumed();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);

            subject.ParentSuspendState.Should().BeNull();
        }

        [Fact]
        public void Apply__Given_ParentTerminated__Then_Updated()
        {
            var subject = new PlanItemStore();

            var @event = new ParentTerminated();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);
        }

        [Fact]
        public void Apply__Given_UserCompletableCriteriaMet__Then_Updated()
        {
            var subject = new PlanItemStore();

            var @event = new UserCompletableCriteriaMet();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);

            subject.UserCompletable.Should().BeTrue();
        }

        [Fact]
        public void Apply__Given_FullyCompleteCriteriaMet__Then_Updated()
        {
            var subject = new PlanItemStore();

            var @event = new FullyCompleteCriteriaMet();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);
        }

        [Fact]
        public void Apply__Given_Repeated__Then_UpdatedAndRepeated()
        {
            var subject = new PlanItemStore();

            var @event = new Repeated();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);
            subject.Repeated.Should().BeTrue();
        }

        [Fact]
        public void Apply__Given_ChildRepeated__Then_Updated()
        {
            var subject = new PlanItemStore();

            var @event = new ChildRepeated();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);
        }

        [Theory, AutoData]
        public void Apply__Given_ChildCreated__Then_UpdatedAndChildIndexed(string instanceId, string definitionId, int repetition)
        {
            var subject = new PlanItemStore();

            subject.Apply(new Defined
            {
                CaseDefinitionId = Guid.NewGuid(),
                Definition = new Interfaces.Model.PlanItem(),
                PlanItemDefinition = new Stage(),
                Repetition = 0
                
            });

            var @event = new ChildCreated
            {
                PlanItemInstanceId = instanceId,
                PlanItemDefinitionId = definitionId,
                Repetition = repetition
            };

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.IsSameOrEqualTo(@event.Updated);
            subject.BehaviorExtension.Should().BeOfType<StageBehaviorStore>();

            var stageStore = subject.BehaviorExtension.As<StageBehaviorStore>();

            stageStore.Children.Should().NotBeEmpty()
                .And.HaveCount(1)
                .And.ContainKey(definitionId);

            stageStore.Children[definitionId].Should().NotBeNull()
                .And.HaveCount(1)
                .And.ContainKey(instanceId)
                .And.ContainValue(repetition);
        }
    }
}
