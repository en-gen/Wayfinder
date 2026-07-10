using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.StateMachine;

namespace Flow.Grains.Plan.PlanItem.Behaviors
{
    // 8.4.1 - Case Instance Lifecyle
    // ~~~~~
    // The outermost Stage instance is special in two areas: (1) It MUST NOT contain entry
    // criteria. (2) That Stage instance implements the Case lifecycle described in this sub
    // clause, which is different than the lifecycle for all other Stage instances.
    //
    // Table 8.6 - Case instance transitions
    // ~~~~~
    // create: [Ø -> Active] Transition to the initial state (Active) when the Case instance
    // is created. The outermost Stage instance skips the Available state and MUST transition
    // directly to the Active state, because that Stage instance does not have a (entry
    // criteria) Sentry.
    //
    // Table 5.31 - PlanItemTransition enumeration
    // ~~~~~
    // create: The casePlanModel transitions from the initial state to Active. The PlanItem
    // transitions from the initial state to Available.
    //
    // PlanItemStateMachine.ConfigureForCasePlanModel already wires Uninitialized -[Create]->
    // Active for this Stage (as opposed to ConfigureForStageOrTask's Uninitialized -[Create]->
    // Available -[Start/ManualStart]-> Active). So for the CasePlanModel, Create is the
    // trigger that arrives at Active - the same semantic arrival StageBehavior's
    // HandleEnterActiveFromStart already handles for Start/ManualStart, just reached by a
    // different, spec-mandated route because this Stage has no Available state to pass
    // through. No additional transition sequence is introduced: child PlanItem instantiation
    // is reused as-is from StageBehavior (see HandleEnterActiveFromStart), same addressing,
    // same Define/Trigger(Create) construction, same exclusion of PlanningTable
    // DiscretionaryItems.
    public class CasePlanModelBehavior : StageBehavior
    {
        public CasePlanModelBehavior(IBehaviorHost host, Stage planItemDefinition, IPlanItemStateMachine stateMachine) :
            base(host, planItemDefinition, stateMachine)
        {
            StateMachine.Configure(PlanItemState.Active)
                .OnEntryFromAsync(PlanItemTransition.Create, HandleEnterActiveFromStart);
        }
    }
}
