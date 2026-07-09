using Flow.Grains.Executables;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Interfaces.Plan.CaseFileItem;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Interfaces.Plan.PlanItem.Behaviors;
using Flow.Grains.Plan.Case;
using Flow.Grains.Plan.CaseFileItem;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Behaviors.Stores;

namespace Flow.Grains.Infrastructure.Mapping
{
    // Hand-written replacement for the AutoMapper CaseFlowProfile (removed: license + CVE
    // exposure, see work item #50). The mapping surface is small and fixed-shape, so this is
    // maintained by hand rather than via a mapping library. Each ToSnapshot() mirrors exactly
    // the member-for-member behavior AutoMapper's convention-based mapping produced, including
    // the two custom configurations called out below.
    public static class SnapshotMapper
    {
        public static Iso8601Snapshot ToSnapshot(this Iso8601 src)
        {
            if (src == null) return null;

            return new Iso8601Snapshot
            {
                RawValue = src.RawValue,
                HasRepetitions = src.HasRepetitions,
                Repetitions = src.Repetitions,
                Start = src.Start,
                End = src.End,
                Duration = src.Duration
            };
        }

        public static CriterionSnapshot ToSnapshot(this CriterionStore src)
        {
            if (src == null) return null;

            return new CriterionSnapshot
            {
                SatisfiedByAddress = src.SatisfiedByAddress,
                State = src.State
            };
        }

        public static StageBehaviorSnapshot ToSnapshot(this StageBehaviorStore src)
        {
            if (src == null) return null;

            return new StageBehaviorSnapshot
            {
                Children = src.Children
            };
        }

        public static TimerEventListenerBehaviorSnapshot ToSnapshot(this TimerEventListenerBehaviorStore src)
        {
            if (src == null) return null;

            return new TimerEventListenerBehaviorSnapshot
            {
                TimerStart = src.TimerStart,
                TimerSchedule = src.TimerSchedule.ToSnapshot(),
                TimerScheduleEvaluationError = src.TimerScheduleEvaluationError
            };
        }

        public static PlanItemSnapshot ToSnapshot(this PlanItemStore src)
        {
            if (src == null) return null;

            return new PlanItemSnapshot
            {
                Definition = src.Definition,
                PlanItemDefinition = src.PlanItemDefinition,

                UserCompletable = src.UserCompletable,
                Required = src.Required,
                RequiredEvaluationError = src.RequiredEvaluationError,

                Repeatable = src.Repeatable,
                RepeatableEvaluationError = src.RepeatableEvaluationError,
                Repeated = src.Repeated,
                Repetition = src.Repetition,

                ManuallyActivatable = src.ManuallyActivatable,
                ManuallyActivatableEvaluationError = src.ManuallyActivatableEvaluationError,

                PlanItemState = src.PlanItemState,
                ParentSuspendState = src.ParentSuspendState,

                EntryCriterionStore = src.EntryCriterionStore.ToSnapshot(),
                ExitCriterionStore = src.ExitCriterionStore.ToSnapshot(),

                BehaviorExtension = MapBehaviorExtension(src.BehaviorExtension)
            };
        }

        public static CaseSnapshot ToSnapshot(this CaseStore src)
        {
            if (src == null) return null;

            return new CaseSnapshot
            {
                Definition = src.Definition,
                CasePlanModel = (Interfaces.Model.Stage)src.PlanItemDefinition,

                UserCompletable = src.UserCompletable,
                Required = src.Required,
                RequiredEvaluationError = src.RequiredEvaluationError,

                Repeatable = src.Repeatable,
                RepeatableEvaluationError = src.RepeatableEvaluationError,
                Repeated = src.Repeated,
                Repetition = src.Repetition,

                ManuallyActivatable = src.ManuallyActivatable,
                ManuallyActivatableEvaluationError = src.ManuallyActivatableEvaluationError,

                PlanItemState = src.PlanItemState,
                ParentSuspendState = src.ParentSuspendState,

                EntryCriterionStore = src.EntryCriterionStore.ToSnapshot(),
                ExitCriterionStore = src.ExitCriterionStore.ToSnapshot(),

                // CaseStore.BehaviorExtension is always a StageBehaviorStore (see
                // CaseStore.Apply(CaseCreated)); CaseSnapshot.BehaviorExtension is typed
                // StageBehaviorSnapshot directly (no polymorphism here, unlike PlanItemSnapshot).
                // A direct cast (rather than `as`) preserves AutoMapper's original failure mode:
                // it would have thrown rather than silently producing a null snapshot here.
                BehaviorExtension = ((StageBehaviorStore)src.BehaviorExtension).ToSnapshot()
            };
        }

        public static CaseFileItemSnapshot ToSnapshot(this CaseFileItemStore src)
        {
            if (src == null) return null;

            return new CaseFileItemSnapshot
            {
                Definition = src.Definition,
                CaseFileItemState = src.CaseFileItemState,
                Value = src.Value
            };
        }

        // Mirrors CaseFlowProfile.MapBehaviorStore: PlanItemStore.BehaviorExtension is a
        // polymorphic object (StageBehaviorStore | TimerEventListenerBehaviorStore | null)
        // depending on which PlanItemDefinition kind defined the PlanItem.
        private static object MapBehaviorExtension(object behaviorExtension)
        {
            switch (behaviorExtension)
            {
                case StageBehaviorStore sbs:
                    return sbs.ToSnapshot();
                case TimerEventListenerBehaviorStore telbs:
                    return telbs.ToSnapshot();
                default:
                    return null;
            }
        }
    }
}
