using System;
using System.Collections.Generic;
using System.Text;
using AutoFixture.Xunit2;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan.Case;
using Flow.Grains.Plan.Case.Events;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors.Stores;
using Flow.Grains.Plan.PlanItem.Events;
using FluentAssertions;
using FluentAssertions.Common;
using Xunit;

namespace Flow.Grains.Tests.Plan.Case
{
    public class CaseStoreTests
    {
        [Fact]
        public void Ctor__Then_InitialState()
        {
            var subject = new CaseStore();

            subject.Created.Should().NotHaveValue();
            subject.Updated.Should().NotHaveValue();

            subject.CaseDefinitionId.Should().BeNull();
            subject.Definition.Should().BeNull();
            subject.TenantId.Should().Be(Guid.Empty);

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
        public void Apply__Given_CaseCreated__Then_UpdateState()
        {
            var caseDefId = ShortGuid.NewGuid();
            var casePlanModel = new Stage();
            var @case = new Interfaces.Model.Case
            {
                CasePlanModel = casePlanModel
            };
            var tenantId = Guid.NewGuid();

            var @event = new CaseCreated
            {
                CaseDefinitionId = caseDefId,
                Definition = @case,
                Repetition = 0,
                TenantId = tenantId
            };

            var subject = new CaseStore();

            subject.Apply(@event);

            subject.Created.Should().HaveValue()
                .And.Be(@event.Created);

            subject.Updated.Should().BeNull();

            subject.Defined.Should().BeTrue();

            subject.CaseDefinitionId.Should().NotBeEmpty()
                .And.Be(caseDefId);

            subject.Definition.Should().NotBeNull()
                .And.Be(@case);

            subject.PlanItemDefinition.Should().NotBeNull()
                .And.Be(casePlanModel);

            subject.BehaviorExtension.Should().NotBeNull()
                .And.BeOfType<StageBehaviorStore>();

            subject.PlanItemState.Should().Be(PlanItemState.Uninitialized);

            // ADO #33 - CaseCreated.TenantId (stamped by CaseGrain.Create from
            // CaseRequestContext.TenantId) must project onto CaseStore.TenantId, the value
            // CaseGrain.Trigger/GetSnapshot compare against the caller's tenant.
            subject.TenantId.Should().Be(tenantId);
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

            var subject = new CaseStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

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

            var subject = new CaseStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

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

            var subject = new CaseStore();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

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
            var subject = new CaseStore();

            var @event = new RequiredRuleEvaluated
            {
                Result = ruleResult
            };

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

            subject.Required.Should().Be(ruleResult);
        }

        [Fact]
        public void Apply__Given_ManualActivationRuleEvaluated__Then_Updated()
        {
            var subject = new CaseStore();

            var @event = new ManualActivationRuleEvaluated();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);
        }

        [Fact]
        public void Apply__Given_RepetitionRuleEvaluated__Then_Updated()
        {
            var subject = new CaseStore();

            var @event = new RepetitionRuleEvaluated();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);
        }

        // 8.6.4 RepetitionRule
        // ~~~~~
        // "That first instantiation ... is not considered a repetition and therefore the value of
        // the RepetitionRule's condition is discarded." Mirrors PlanItemStore's handling - the
        // CaseStore projection applies the same event type for the outermost Stage.
        [Fact]
        public void Apply__Given_RepetitionRuleEvaluated_Discarded__Then_RepeatableUnchanged()
        {
            var subject = new CaseStore();

            var @event = new RepetitionRuleEvaluated
            {
                Result = true,
                Discard = true
            };

            subject.Apply(@event);

            subject.Repeatable.Should().BeFalse("a discarded first evaluation must not persist its Result as Repeatable");
            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated, "the event still marks the store as updated for audit purposes");
        }

        [Fact]
        public void Apply__Given_ParentSuspended__Then_ParentSuspendedStateSet()
        {
            var preSuspendedState = PlanItemState.Enabled;

            var subject = new CaseStore();
            subject.Apply(new Transitioned
            {
                Source = PlanItemState.Available,
                Trigger = PlanItemTransition.Enable,
                Destination = preSuspendedState
            });

            var @event = new ParentSuspended();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

            subject.ParentSuspendState.Should().Be(preSuspendedState);
        }

        [Fact]
        public void Apply__Given_ParentResumed__Then_ParentSuspendedStateNull()
        {
            var preSuspendedState = PlanItemState.Enabled;

            var subject = new CaseStore();
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
                .And.Be(@event.Updated);

            subject.ParentSuspendState.Should().BeNull();
        }

        [Fact]
        public void Apply__Given_ParentTerminated__Then_Updated()
        {
            var subject = new CaseStore();

            var @event = new ParentTerminated();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);
        }

        [Fact]
        public void Apply__Given_UserCompletableCriteriaMet__Then_Updated()
        {
            var subject = new CaseStore();

            var @event = new UserCompletableCriteriaMet();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);

            subject.UserCompletable.Should().BeTrue();
        }

        [Fact]
        public void Apply__Given_FullyCompleteCriteriaMet__Then_Updated()
        {
            var subject = new CaseStore();

            var @event = new FullyCompleteCriteriaMet();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);
        }

        [Fact]
        public void Apply__Given_Repeated__Then_UpdatedAndRepeated()
        {
            var subject = new CaseStore();

            var @event = new Repeated();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);
            subject.Repeated.Should().BeTrue();
        }

        [Fact]
        public void Apply__Given_ChildRepeated__Then_Updated()
        {
            var subject = new CaseStore();

            var @event = new ChildRepeated();

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);
        }

        [Theory, AutoData]
        public void Apply__Given_ChildCreated__Then_UpdatedAndChildIndexed(string instanceId, string definitionId, int repetition)
        {
            var subject = new CaseStore();

            subject.Apply(new CaseCreated
            {
                CaseDefinitionId = ShortGuid.NewGuid(),
                Definition = new Interfaces.Model.Case(),
                Repetition = 0
            });

            var @event = new ChildCreated
            {
                PlanItemInstanceId = instanceId,
                PlanItemId = definitionId,
                Repetition = repetition
            };

            subject.Apply(@event);

            subject.Updated.Should().HaveValue()
                .And.Be(@event.Updated);
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
