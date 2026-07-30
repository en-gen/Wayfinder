using System;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.CmmnElement;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem
{
    [GenerateSerializer]
    public class PlanItemStore : CmmnElementStore<Interfaces.Model.PlanItem>, IBehaviorStore
    {
        [Id(0)]
        public PlanItemDefinition PlanItemDefinition { get; private set; }

        [Id(1)]
        public bool UserCompletable { get; private set; }

        [Id(2)]
        public bool Required { get; private set; }
        [Id(3)]
        public string RequiredEvaluationError { get; private set; }

        [Id(4)]
        public bool Repeatable { get; private set; }
        [Id(5)]
        public string RepeatableEvaluationError { get; private set; }
        [Id(6)]
        public bool Repeated { get; private set; }
        [Id(7)]
        public int Repetition { get; private set; }

        [Id(8)]
        public bool ManuallyActivatable { get; private set; }
        [Id(9)]
        public string ManuallyActivatableEvaluationError { get; private set; }

        [Id(10)]
        public PlanItemState PlanItemState { get; private set; }
        [Id(11)]
        public PlanItemState? ParentSuspendState { get; private set; }

        [Id(12)]
        public CriterionStore EntryCriterionStore { get; } = new CriterionStore();
        [Id(13)]
        public CriterionStore ExitCriterionStore { get; } = new CriterionStore();

        [Id(14)]
        public object BehaviorExtension { get; private set; }

        // #63: the parent's definition id at the moment this child was defined (StageBehavior.
        // CreateChild's Host.DefinitionId) - persisted so a reactivated grain's Activate/Resume
        // re-arms the parent-transition subscription on the correct stream key. Null for the
        // CasePlanModel root and for items defined via the bare 2-arg Define overload.
        [Id(15)]
        public string ParentDefinitionId { get; private set; }

        // #65: the parent's full DEFINITION-scope path (StageBehavior.CreateChild's
        // Host.DefinitionScope) at the moment this child was defined - persisted so
        // CaseDefinitionGrain.GetPlanItemDefinition is searched with a definition-id path instead
        // of the runtime instance-id Scope (which only coincidentally resolves root-level
        // declarations). Null for the CasePlanModel root and for items defined via the bare 2-arg
        // Define overload (PlanItemGrain.DefineRepetition falls back to the instance Scope then).
        [Id(16)]
        public string ParentDefinitionScope { get; private set; }

        public void Apply(Defined @event)
        {
            base.Apply(@event);
            PlanItemDefinition = @event.PlanItemDefinition;
            Repetition = @event.Repetition;
            ParentDefinitionId = @event.ParentDefinitionId;
            ParentDefinitionScope = @event.ParentDefinitionScope;

            switch (PlanItemDefinition)
            {
                case Stage s:
                    {
                        BehaviorExtension = new StageBehaviorStore();
                        break;
                    }
                case TimerEventListener tel:
                    {
                        BehaviorExtension = new TimerEventListenerBehaviorStore();
                        break;
                    }
            }
        }

        public void Apply(Transitioned @event)
        {
            PlanItemState = @event.Destination;
            Updated = @event.Updated;
        }

        public void Apply(EntryCriterionSatisfied @event)
        {
            EntryCriterionStore.Apply(@event);
            Updated = @event.Updated;
        }

        public void Apply(ExitCriterionSatisfied @event)
        {
            ExitCriterionStore.Apply(@event);
            Updated = @event.Updated;
        }

        public void Apply(RequiredRuleEvaluated @event)
        {
            Updated = @event.Updated;

            Required = @event.Result;
            RequiredEvaluationError = @event.Error;
        }

        public void Apply(RepetitionRuleEvaluated @event)
        {
            Updated = @event.Updated;

            // 8.6.4 - the first (Create -> Available) evaluation's result MUST be discarded, not
            // treated as the item's repeatable determination - see RepetitionRuleEvaluated.Discard.
            if (@event.Discard) return;

            Repeatable = @event.Result;
            RepeatableEvaluationError = @event.Error;
        }

        public void Apply(ManualActivationRuleEvaluated @event)
        {
            Updated = @event.Updated;

            ManuallyActivatable = @event.Result;
            ManuallyActivatableEvaluationError = @event.Error;
        }

        public void Apply(ParentSuspended @event)
        {
            ParentSuspendState = PlanItemState;
            Updated = @event.Updated;
        }

        public void Apply(ParentResumed @event)
        {
            ParentSuspendState = null;
            Updated = @event.Updated;
        }

        public void Apply(UserCompletableCriteriaMet @event)
        {
            Updated = @event.Updated;
            UserCompletable = @event.UserCompletable;
        }

        public void Apply(Repeated @event)
        {
            Updated = @event.Updated;
            Repeated = true;
        }

        public void Apply(ChildCreated @event)
        {
            Updated = @event.Updated;

            if (BehaviorExtension is StageBehaviorStore stageStore)
            {
                stageStore.Apply(@event);
            }
        }

        // #161 - same delegation shape as Apply(ChildCreated) above: the redelivery guard's
        // recorded source-instance-id set lives on the nested StageBehaviorStore, not here.
        public void Apply(ChildRepeated @event)
        {
            Updated = @event.Updated;

            if (BehaviorExtension is StageBehaviorStore stageStore)
            {
                stageStore.Apply(@event);
            }
        }

        // #178 - same delegation shape as Apply(ChildCreated)/Apply(ChildRepeated) above: the
        // buffered-repetition queue lives on the nested StageBehaviorStore, not here.
        public void Apply(RepetitionBuffered @event)
        {
            Updated = @event.Updated;

            if (BehaviorExtension is StageBehaviorStore stageStore)
            {
                stageStore.Apply(@event);
            }
        }

        public void Apply(RepetitionBufferDrained @event)
        {
            Updated = @event.Updated;

            if (BehaviorExtension is StageBehaviorStore stageStore)
            {
                stageStore.Apply(@event);
            }
        }

        public void Apply(TimerStartTriggerOccurred @event)
        {
            Updated = @event.Updated;

            if (BehaviorExtension is TimerEventListenerBehaviorStore timerStore)
            {
                timerStore.Apply(@event);
            }
        }

        public void Apply(TimerExpressionEvaluated @event)
        {
            Updated = @event.Updated;

            if (BehaviorExtension is TimerEventListenerBehaviorStore timerStore)
            {
                timerStore.Apply(@event);
            }
        }
    }
}
