using System.Linq;
using System.Threading.Tasks;
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
            // Table 8.6 - re-activate (Completed/Terminated/Failed/Suspended -> Active,
            // "Transition by a Case worker (human), or an administrator") deliberately has NO
            // entry action of its own: the case's top-level PlanItems already exist from the
            // original Create (below), and Table 8.6 assigns re-activation no re-instantiation
            // semantics - re-running the Create entry action would duplicate every top-level
            // child. The transition itself is already permitted by
            // PlanItemStateMachine.ConfigureForCasePlanModel.
            StateMachine.Configure(PlanItemState.Active)
                .OnEntryFromAsync(PlanItemTransition.Create, HandleEnterActiveFromStart);

            // 8.4.1/Table 8.5 - Closed: "Terminal state. In this state no new activity is allowed
            // in the Case. The Case instance caseFileModel and all its content becomes read only,
            // and no new Task or Stage instances can be planned." PlanItemStateMachine.
            // ConfigureForCasePlanModel already has no outgoing Permit(...) edges from Closed, so
            // the state machine itself refuses every further transition (silently, via
            // HandleUnhandledTrigger) - see CaseGrain.Trigger's explicit guard for the loud,
            // observable half of this immutability. What was missing is any entry action AT ALL
            // on arrival at Closed: reusing HandleEnterTerminal (BaseBehavior) guarantees the
            // exit-criteria subscription cleanup that Completed/Terminated already perform on
            // their own entry also runs for a case closed from Suspended or Failed (neither of
            // which run it), so nothing is left listening once the case is terminally shut.
            StateMachine.Configure(PlanItemState.Closed)
                .OnEntryAsync(HandleEnterTerminal);
        }

        // Table 8.5/Table 8.6 - complete: "The Case instance is completed, when all the required
        // Milestone, Stage, and Task instances in the outermost Stage instance are completed
        // (completed or terminated), and there are no executing (Active) Stage or Task
        // instances." Unlike an ordinary Stage's Table 8.12 manual branch (which drops the
        // no-Active-children conjunct - see the base remarks), the CASE lifecycle's own
        // completion criteria keep it, regardless of the autoComplete attribute: manually
        // completing the case root requires BOTH no Active children AND all required children
        // terminal. (Judging direct children covers the tree: an Active instance nested deeper
        // inside a non-Active top-level child cannot exist, because a Stage containing an Active
        // instance is itself Active - Table 8.7.)
        protected override async Task<bool> ManualCompletionCriteriaSatisfied()
        {
            var childSnapshots = await GetChildSnapshots();

            return childSnapshots.All(x => x.PlanItemState != PlanItemState.Active) &&
                   childSnapshots.Where(x => x.Required).All(x => x.PlanItemState.IsTerminal());
        }
    }
}
