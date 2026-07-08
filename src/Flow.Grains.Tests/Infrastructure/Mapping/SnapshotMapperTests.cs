using System;
using System.Collections.Generic;
using System.Reflection;
using AutoFixture.Xunit2;
using Flow.Grains.Infrastructure.Mapping;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Interfaces.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.Case;
using Flow.Grains.Plan.Case.Events;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors.Stores;
using Flow.Grains.Plan.PlanItem.Events;
using FluentAssertions;
using NodaTime;
using NodaTime.Text;
using Xunit;

namespace Flow.Grains.Tests.Infrastructure.Mapping
{
    // Replaces the AssertConfigurationIsValid-style coverage the removed AutoMapper profile
    // would have given us for free. Hand-written mapping's main risk is a silently-dropped
    // member (a snapshot property nobody remembered to set), so alongside asserting the custom
    // routing logic explicitly, every test here also runs the mapped snapshot through a
    // reflection-based "every property is populated" sweep.
    public class SnapshotMapperTests
    {
        // Snapshot types this sweep knows how to recurse into. Anything outside this set
        // (Model.PlanItem, Model.Case, PlanItemDefinition, Stage, ...) is treated as an opaque
        // leaf - its own internal shape isn't part of SnapshotMapper's contract, only whether
        // the reference made it across at all.
        //
        // Iso8601Snapshot is deliberately excluded: ISO 8601 intervals are <start>/<end> XOR
        // <start>/<duration> XOR <duration>/<end> - Start/End and Duration can never all be
        // non-null on the same instance, so a blanket "every property populated" sweep can never
        // pass for it. Its coverage instead comes from ToSnapshot__Given_Iso8601__Then_
        // AllMembersMapped below, which asserts every property by value (including the
        // legitimately-null ones) rather than by "is it populated".
        private static readonly ISet<Type> RecognizedSnapshotTypes = new HashSet<Type>
        {
            typeof(CriterionSnapshot),
            typeof(StageBehaviorSnapshot),
            typeof(TimerEventListenerBehaviorSnapshot),
            typeof(PlanItemSnapshot),
            typeof(CaseSnapshot)
        };

        // Walks every public property on a fully-populated snapshot instance (recursing into
        // nested snapshot types) and fails if any is left at its default value. This is the
        // safety net for hand mapping: it catches a property added to a Snapshot/Store pair in
        // the future that nobody wired up in SnapshotMapper. Boolean members are exempted - a
        // bare "not default(bool)" check can't distinguish "set" from "defaulted to false" - so
        // every bool is instead asserted explicitly by value in each test.
        private static void AssertAllPropertiesPopulated(object instance, string path)
        {
            instance.Should().NotBeNull(because: $"{path} should not be null");

            var type = instance.GetType();
            RecognizedSnapshotTypes.Should().Contain(type,
                because: $"{path} ({type.Name}) should be a recognized snapshot type - add it to {nameof(RecognizedSnapshotTypes)} if this is a new mapped type");

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propertyPath = $"{path}.{property.Name}";
                var value = property.GetValue(instance);

                if (type == typeof(StageBehaviorSnapshot) && property.Name == nameof(StageBehaviorSnapshot.Children))
                {
                    value.Should().NotBeNull(propertyPath);
                    ((IDictionary<string, IDictionary<string, int>>)value).Should().NotBeEmpty(propertyPath);
                    continue;
                }

                AssertLeafPropertyPopulated(value, propertyPath);
            }
        }

        private static void AssertLeafPropertyPopulated(object value, string propertyPath)
        {
            if (value is bool) return; // see method comment above

            value.Should().NotBeNull(propertyPath);

            switch (value)
            {
                case string s:
                    s.Should().NotBeNullOrEmpty(propertyPath);
                    break;
                case Enum e:
                    Convert.ToInt64(e).Should().NotBe(0, propertyPath);
                    break;
                case DateTime dt:
                    dt.Should().NotBe(default, propertyPath);
                    break;
                case int i:
                    i.Should().NotBe(0, propertyPath);
                    break;
                default:
                    if (RecognizedSnapshotTypes.Contains(value.GetType()))
                    {
                        AssertAllPropertiesPopulated(value, propertyPath);
                    }
                    break;
            }
        }

        // R#/<start>/<duration>: exercises every Iso8601 member at once (Repetitions,
        // HasRepetitions, Start, Duration all non-default; only End is legitimately null - see
        // the RecognizedSnapshotTypes comment on why Iso8601Snapshot skips the generic sweep).
        [Theory, AutoData]
        public void ToSnapshot__Given_Iso8601__Then_AllMembersMapped
            (int repetitions, int days, int hours, int minutes, int seconds)
        {
            var start = DateTime.UtcNow;
            var period = new PeriodBuilder
            {
                Days = days,
                Hours = hours,
                Minutes = minutes,
                Seconds = seconds
            }.Build().Normalize();

            var isoString = $"R{repetitions}/{start:O}/{PeriodPattern.NormalizingIso.Format(period)}";
            var iso = new Flow.Grains.Executables.Iso8601(isoString);

            var snapshot = iso.ToSnapshot();

            snapshot.RawValue.Should().Be(iso.RawValue)
                .And.Be(isoString);
            snapshot.HasRepetitions.Should().Be(iso.HasRepetitions)
                .And.BeTrue();
            snapshot.Repetitions.Should().Be(iso.Repetitions)
                .And.Be(repetitions);
            snapshot.Start.Should().Be(iso.Start)
                .And.Be(start);
            snapshot.End.Should().Be(iso.End)
                .And.BeNull();
            snapshot.Duration.Should().Be(iso.Duration)
                .And.Be(period.ToDuration().ToTimeSpan());
        }

        [Fact]
        public void ToSnapshot__Given_Null_Iso8601__Then_Null()
        {
            ((Flow.Grains.Executables.Iso8601)null).ToSnapshot().Should().BeNull();
        }

        [Theory, AutoData]
        public void ToSnapshot__Given_CriterionStore__Then_AllMembersMapped
            (string sourceScope, string sourceId)
        {
            var store = new CriterionStore();
            store.Apply(new EntryCriterionSatisfied
            {
                SourceScope = sourceScope,
                SourceId = sourceId,
                OnPartOccurred = true
            });

            var snapshot = store.ToSnapshot();

            snapshot.SatisfiedByAddress.Should().Be(store.SatisfiedByAddress)
                .And.Be($"{sourceScope}.{sourceId}");
            snapshot.State.Should().Be(store.State);
            snapshot.State.Should().HaveFlag(CriterionState.Satisfied)
                .And.HaveFlag(CriterionState.OnPartOccurred);

            AssertAllPropertiesPopulated(snapshot, nameof(CriterionSnapshot));
        }

        [Fact]
        public void ToSnapshot__Given_Null_CriterionStore__Then_Null()
        {
            ((CriterionStore)null).ToSnapshot().Should().BeNull();
        }

        [Theory, AutoData]
        public void ToSnapshot__Given_StageBehaviorStore__Then_AllMembersMapped
            (string definitionId, string instanceId, int repetition)
        {
            var store = new StageBehaviorStore();
            store.Apply(new ChildCreated
            {
                PlanItemDefinitionId = definitionId,
                PlanItemInstanceId = instanceId,
                Repetition = repetition + 1 // AssertAllPropertiesPopulated treats 0 as unset
            });

            var snapshot = store.ToSnapshot();

            snapshot.Children.Should().BeSameAs(store.Children);
            snapshot.Children.Should().ContainKey(definitionId);
            snapshot.Children[definitionId].Should().ContainKey(instanceId)
                .And.ContainValue(repetition + 1);

            AssertAllPropertiesPopulated(snapshot, nameof(StageBehaviorSnapshot));
        }

        [Fact]
        public void ToSnapshot__Given_Null_StageBehaviorStore__Then_Null()
        {
            ((StageBehaviorStore)null).ToSnapshot().Should().BeNull();
        }

        [Theory, AutoData]
        public void ToSnapshot__Given_TimerEventListenerBehaviorStore__Then_AllMembersMapped
            (DateTime timerStart, string rawIso, string evaluationError)
        {
            var store = new TimerEventListenerBehaviorStore();
            store.Apply(new TimerStartTriggerOccurred { Occurred = timerStart });
            store.Apply(new TimerExpressionEvaluated
            {
                Result = BuildIso8601(rawIso),
                Error = evaluationError
            });

            var snapshot = store.ToSnapshot();

            snapshot.TimerStart.Should().Be(store.TimerStart);
            snapshot.TimerScheduleEvaluationError.Should().Be(store.TimerScheduleEvaluationError);
            snapshot.TimerSchedule.Should().NotBeNull();
            snapshot.TimerSchedule.RawValue.Should().Be(store.TimerSchedule.RawValue);

            AssertAllPropertiesPopulated(snapshot, nameof(TimerEventListenerBehaviorSnapshot));
        }

        [Fact]
        public void ToSnapshot__Given_Null_TimerEventListenerBehaviorStore__Then_Null()
        {
            ((TimerEventListenerBehaviorStore)null).ToSnapshot().Should().BeNull();
        }

        [Fact]
        public void ToSnapshot__Given_Null_PlanItemStore__Then_Null()
        {
            ((PlanItemStore)null).ToSnapshot().Should().BeNull();
        }

        [Fact]
        public void ToSnapshot__Given_Null_CaseStore__Then_Null()
        {
            ((CaseStore)null).ToSnapshot().Should().BeNull();
        }

        // Every scalar member on PlanItemStore/PlanItemSnapshot, driven fully populated so the
        // reflection sweep below has something non-default to find if a property is dropped.
        [Theory, AutoData]
        public void ToSnapshot__Given_PlanItemStore_WithStageBehavior__Then_AllMembersMappedIncludingPolymorphicBehaviorExtension
            (string caseDefinitionId, string definitionId, string instanceId, int childRepetition,
                string requiredError, string repeatableError, string manuallyActivatableError,
                string entrySourceScope, string entrySourceId, string exitSourceScope, string exitSourceId)
        {
            var definition = new Flow.Grains.Interfaces.Model.PlanItem { Id = definitionId };
            var stage = new Stage();

            var store = new PlanItemStore();
            store.Apply(new Defined
            {
                CaseDefinitionId = caseDefinitionId,
                Definition = definition,
                PlanItemDefinition = stage,
                Repetition = 1
            });
            store.Apply(new Transitioned { Destination = PlanItemState.Active });
            store.Apply(new RequiredRuleEvaluated { Result = true, Error = requiredError });
            store.Apply(new RepetitionRuleEvaluated { Result = true, Error = repeatableError });
            store.Apply(new ManualActivationRuleEvaluated { Result = true, Error = manuallyActivatableError });
            store.Apply(new ParentSuspended());
            store.Apply(new UserCompletableCriteriaMet { UserCompletable = true });
            store.Apply(new Repeated());
            store.Apply(new EntryCriterionSatisfied
            {
                SourceScope = entrySourceScope,
                SourceId = entrySourceId,
                OnPartOccurred = true
            });
            store.Apply(new ExitCriterionSatisfied
            {
                SourceScope = exitSourceScope,
                SourceId = exitSourceId,
                OnPartOccurred = true
            });
            store.Apply(new ChildCreated
            {
                PlanItemDefinitionId = definitionId,
                PlanItemInstanceId = instanceId,
                Repetition = childRepetition + 1
            });

            var snapshot = store.ToSnapshot();

            snapshot.Definition.Should().BeSameAs(store.Definition);
            snapshot.PlanItemDefinition.Should().BeSameAs(store.PlanItemDefinition);
            snapshot.UserCompletable.Should().BeTrue();
            snapshot.Required.Should().BeTrue();
            snapshot.RequiredEvaluationError.Should().Be(requiredError);
            snapshot.Repeatable.Should().BeTrue();
            snapshot.RepeatableEvaluationError.Should().Be(repeatableError);
            snapshot.Repeated.Should().BeTrue();
            snapshot.Repetition.Should().Be(1);
            snapshot.ManuallyActivatable.Should().BeTrue();
            snapshot.ManuallyActivatableEvaluationError.Should().Be(manuallyActivatableError);
            snapshot.PlanItemState.Should().Be(PlanItemState.Active);
            snapshot.ParentSuspendState.Should().Be(PlanItemState.Active);

            // Custom logic under test: PlanItemStore.BehaviorExtension is a polymorphic object
            // (StageBehaviorStore here) that CaseFlowProfile.MapBehaviorStore used to route by
            // runtime type; SnapshotMapper.MapBehaviorExtension must route it identically.
            snapshot.BehaviorExtension.Should().BeOfType<StageBehaviorSnapshot>();
            var behaviorSnapshot = (StageBehaviorSnapshot)snapshot.BehaviorExtension;
            behaviorSnapshot.Children.Should().BeSameAs(((StageBehaviorStore)store.BehaviorExtension).Children);

            AssertAllPropertiesPopulated(snapshot, nameof(PlanItemSnapshot));
        }

        // Same PlanItemStore->PlanItemSnapshot mapping, but drives the TimerEventListener
        // behavior arm of the polymorphic BehaviorExtension routing so both switch branches in
        // SnapshotMapper.MapBehaviorExtension are exercised.
        [Theory, AutoData]
        public void ToSnapshot__Given_PlanItemStore_WithTimerEventListenerBehavior__Then_PolymorphicBehaviorExtensionRoutedCorrectly
            (string caseDefinitionId, DateTime timerStart, string rawIso, string evaluationError)
        {
            var definition = new Flow.Grains.Interfaces.Model.PlanItem();
            var timerEventListener = new TimerEventListener();
            var iso = BuildIso8601(rawIso);

            var store = new PlanItemStore();
            store.Apply(new Defined
            {
                CaseDefinitionId = caseDefinitionId,
                Definition = definition,
                PlanItemDefinition = timerEventListener,
                Repetition = 0
            });
            store.Apply(new TimerStartTriggerOccurred { Occurred = timerStart });
            store.Apply(new TimerExpressionEvaluated
            {
                Result = iso,
                Error = evaluationError
            });

            var snapshot = store.ToSnapshot();

            snapshot.BehaviorExtension.Should().BeOfType<TimerEventListenerBehaviorSnapshot>();
            var behaviorSnapshot = (TimerEventListenerBehaviorSnapshot)snapshot.BehaviorExtension;
            behaviorSnapshot.TimerStart.Should().Be(timerStart);
            behaviorSnapshot.TimerScheduleEvaluationError.Should().Be(evaluationError);
            behaviorSnapshot.TimerSchedule.RawValue.Should().Be(iso.RawValue);

            AssertAllPropertiesPopulated(behaviorSnapshot, $"{nameof(PlanItemSnapshot.BehaviorExtension)}<{nameof(TimerEventListenerBehaviorSnapshot)}>");
        }

        [Fact]
        public void ToSnapshot__Given_PlanItemStore_WithUndefinedBehaviorExtension__Then_BehaviorExtensionNull()
        {
            var store = new PlanItemStore();

            var snapshot = store.ToSnapshot();

            snapshot.BehaviorExtension.Should().BeNull();
        }

        // CaseStore->CaseSnapshot has two members with configuration different from a plain
        // convention map: CasePlanModel (source PlanItemDefinition -> dest Stage, a narrowing
        // cast) and BehaviorExtension (source object -> dest StageBehaviorSnapshot directly,
        // not the polymorphic routing PlanItemSnapshot needs, since CaseStore's is always a
        // StageBehaviorStore).
        [Theory, AutoData]
        public void ToSnapshot__Given_CaseStore__Then_AllMembersMappedIncludingCasePlanModelAndDirectBehaviorExtension
            (string caseDefinitionId, string stageId, string definitionId, string instanceId, int childRepetition,
                string requiredError, string repeatableError, string manuallyActivatableError,
                string entrySourceScope, string entrySourceId, string exitSourceScope, string exitSourceId)
        {
            var stage = new Stage { Id = stageId };
            var caseDefinition = new Case { Id = caseDefinitionId, CasePlanModel = stage };

            var store = new CaseStore();
            store.Apply(new CaseCreated
            {
                CaseDefinitionId = caseDefinitionId,
                Definition = caseDefinition,
                Repetition = 1
            });
            store.Apply(new Transitioned { Destination = PlanItemState.Active });
            store.Apply(new RequiredRuleEvaluated { Result = true, Error = requiredError });
            store.Apply(new RepetitionRuleEvaluated { Result = true, Error = repeatableError });
            store.Apply(new ManualActivationRuleEvaluated { Result = true, Error = manuallyActivatableError });
            store.Apply(new ParentSuspended());
            store.Apply(new UserCompletableCriteriaMet { UserCompletable = true });
            store.Apply(new Repeated());
            store.Apply(new EntryCriterionSatisfied
            {
                SourceScope = entrySourceScope,
                SourceId = entrySourceId,
                OnPartOccurred = true
            });
            store.Apply(new ExitCriterionSatisfied
            {
                SourceScope = exitSourceScope,
                SourceId = exitSourceId,
                OnPartOccurred = true
            });
            store.Apply(new ChildCreated
            {
                PlanItemDefinitionId = definitionId,
                PlanItemInstanceId = instanceId,
                Repetition = childRepetition + 1
            });

            var snapshot = store.ToSnapshot();

            snapshot.Definition.Should().BeSameAs(store.Definition);

            // Custom logic under test: CaseFlowProfile mapped CasePlanModel from
            // src.PlanItemDefinition (declared type PlanItemDefinition, runtime type Stage).
            snapshot.CasePlanModel.Should().BeSameAs(store.PlanItemDefinition)
                .And.BeSameAs(stage);

            snapshot.UserCompletable.Should().BeTrue();
            snapshot.Required.Should().BeTrue();
            snapshot.RequiredEvaluationError.Should().Be(requiredError);
            snapshot.Repeatable.Should().BeTrue();
            snapshot.RepeatableEvaluationError.Should().Be(repeatableError);
            snapshot.Repeated.Should().BeTrue();
            snapshot.Repetition.Should().Be(1);
            snapshot.ManuallyActivatable.Should().BeTrue();
            snapshot.ManuallyActivatableEvaluationError.Should().Be(manuallyActivatableError);
            snapshot.PlanItemState.Should().Be(PlanItemState.Active);
            snapshot.ParentSuspendState.Should().Be(PlanItemState.Active);

            // Custom logic under test: CaseStore.BehaviorExtension (object, runtime type
            // StageBehaviorStore) maps directly to CaseSnapshot.BehaviorExtension
            // (StageBehaviorSnapshot) - no polymorphic switch needed here, unlike PlanItem's.
            snapshot.BehaviorExtension.Should().NotBeNull();
            snapshot.BehaviorExtension.Children.Should().BeSameAs(((StageBehaviorStore)store.BehaviorExtension).Children);

            AssertAllPropertiesPopulated(snapshot, nameof(CaseSnapshot));
        }

        [Fact]
        public void ToSnapshot__Given_CaseStore_Undefined__Then_CasePlanModelAndBehaviorExtensionNull()
        {
            var store = new CaseStore();

            var snapshot = store.ToSnapshot();

            snapshot.CasePlanModel.Should().BeNull();
            snapshot.BehaviorExtension.Should().BeNull();
        }

        private static Flow.Grains.Executables.Iso8601 BuildIso8601(string discriminator) =>
            new Flow.Grains.Executables.Iso8601(DateTime.UtcNow.AddSeconds(
                Math.Abs(discriminator.GetHashCode()) % 3600).ToString("O"));
    }
}
