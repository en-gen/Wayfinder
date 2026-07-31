using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores;
using Wayfinder.Grains.Plan.PlanItem.StateMachine;
using Orleans.Streams;

namespace Wayfinder.Grains.Plan.PlanItem.Behaviors
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
        // ADO #66 - the CasePlanModel fires `terminate`, not `exit`, for its own exit criteria
        // ~~~~~
        // Table 8.6 (Case instance transitions) has no `exit` row for the casePlanModel at all -
        // its only Active->Terminated edge is `terminate`, which 8.4.1/Table 8.6 says is "achieved
        // by an exit criteria and also allows a Case worker to terminate": the SAME trigger for
        // both a satisfied exit criterion and a human decision. Table 5.31's transition glossary
        // confirms this is deliberate, not an oversight: `exit` is scoped to "the Stage or Task"
        // (the casePlanModel is conspicuously absent from that list), while `terminate` is scoped
        // to "the casePlanModel, Stage, or Task". StageBehavior.HandleSentrySatisfied - shared
        // as-is by this class - fires StageBehavior.ExitCriterionTransition (base value: Exit) when
        // an ExitCriterion's sentry is satisfied; overriding it here to Terminate is the one change
        // needed to make the CasePlanModel spec-faithful, since PlanItemStateMachine.
        // ConfigureForCasePlanModel's Active state already permits Terminate (the Case-worker-
        // decision route) - no additional Permit(...) edge is required, this override just routes
        // the exit-criterion path onto the same edge.
        protected override PlanItemTransition ExitCriterionTransition => PlanItemTransition.Terminate;

        // ADO #66 - see the Active/Create registration above for why this exists as its own
        // entry action. StreamFlags.Create (not Resume): this is the one and only path that
        // establishes the CasePlanModel's ExitCriteria subscription handle in the first place.
        private Task HandleEnterActiveFromCreate() =>
            SubscribeToCriteria(x => x.ExitCriteria, StreamFlags.Create);

        // #178 - see the Reactivate registration in the constructor for the full reasoning. Only
        // drains when this Active arrival is genuinely leaving Suspended (Table 8.6's OTHER three
        // Reactivate sources - Completed/Terminated/Failed - must not trigger a drain).
        private Task DrainPendingRepetitionsIfLeavingSuspended(PlanItemStateMachine.Transition transition) =>
            transition.Source == PlanItemState.Suspended
                ? DrainPendingRepetitions()
                : Task.CompletedTask;

        // ADO #67 - see StageBehavior's own ctor remarks: threaded through to the base
        // constructor unchanged (the CasePlanModel enforces the same ceiling in the inherited
        // HandleChildRepeated), defaulted so existing direct construction keeps compiling.
        public CasePlanModelBehavior(
            IBehaviorHost host,
            Stage planItemDefinition,
            IPlanItemStateMachine stateMachine,
            int repetitionCeiling = RepetitionGuardOptions.DefaultMaxRepetitionsPerPlanItem) :
            base(host, planItemDefinition, stateMachine, repetitionCeiling)
        {
            // Table 8.6 - re-activate (Completed/Terminated/Failed/Suspended -> Active,
            // "Transition by a Case worker (human), or an administrator") does NOT re-run
            // HandleEnterActiveFromStart/HandleEnterActiveFromCreate: the case's top-level
            // PlanItems already exist from the original Create, and Table 8.6 assigns
            // re-activation no re-instantiation semantics - re-running child creation here would
            // duplicate every top-level child. The transition itself is already permitted by
            // PlanItemStateMachine.ConfigureForCasePlanModel.
            //
            // #178 (review round 2, BLOCKER) - it DOES need one entry action, though:
            // StageBehavior's own constructor registers DrainPendingRepetitions on Resume/
            // ParentResume, but ConfigureForCasePlanModel permits ONLY Reactivate + Close out of
            // Suspended - the Case has no Resume/ParentResume edge at all (Table 8.6). Without
            // this registration, a repetition request buffered while the CasePlanModel itself was
            // genuinely Suspended would NEVER drain: not spawned, not dropped, sitting in
            // StageStore.PendingRepetitions (and the journal) permanently - on the single most
            // common repeating-item container, via the ordinary spec path, no race required.
            //
            // Guarded on transition.Source == Suspended, NOT registered unconditionally for
            // Reactivate: Table 8.6 overloads this SAME trigger name across FOUR source states
            // (Completed, Terminated, Failed, Suspended -> Active), and only the Suspended one is
            // a genuine "resume from a preserve-and-restore pause" (Table 8.9 + Figure 8.3 - the
            // same basis StageBehavior.HandleChildRepeated's Suspended branch cites). The other
            // three are human recovery actions from states this fix intentionally does NOT
            // buffer into in the first place (Completed/Terminated refuse outright; Failed
            // refuses-and-records - see HandleChildRepeated's own remarks, especially the
            // fault/re-activate-carries-no-history-semantics argument for Failed) - so a
            // Reactivate arriving from any of those three must not drain anything. In practice the
            // buffer can only be non-empty on a Reactivate-from-Failed when a ceiling breach
            // Faulted this Host mid-drain (see DrainPendingRepetitions' ceilingBreached remarks);
            // replaying THOSE leftover entries on a later Reactivate would immediately re-fault
            // the container - the same refuse-loop-by-construction StageBehavior's own
            // constructor documents for why ordinary Stage/Task deliberately has NO Reactivate
            // hook at all. This guard achieves that same outcome for the CasePlanModel via a
            // source-state check instead, since Reactivate cannot simply be omitted here the way
            // it is for ordinary Stage/Task.
            StateMachine.Configure(PlanItemState.Active)
                .OnEntryFromAsync(PlanItemTransition.Reactivate, DrainPendingRepetitionsIfLeavingSuspended);
            //
            // ADO #66 - CasePlanModel exit criteria were never armed
            // ~~~~~
            // 8.5: "Exit criterion sentries are considered ready for evaluation while the
            // CasePlanModel, Stage, or Task is in Active state." An ordinary Stage arms its
            // ExitCriteria subscription in HandleEnterAvailableFromCreate (StageBehavior, on
            // entry to Available from Create - see that method's D6 remarks) - StreamFlags.
            // Create is what actually establishes the subscription handle; BaseBehavior.
            // Activate's own SubscribeToCriteria(ExitCriteria, StreamFlags.Resume) is a no-op
            // until that handle exists. The CasePlanModel never passes through Available at all
            // (Table 8.6/5.31: Create lands directly on Active - see this class's own remarks
            // above), so HandleEnterAvailableFromCreate's body never runs for it and the
            // subscription was never armed by any path - a Case-level exit criterion becoming
            // satisfied had nothing listening for it. Registered as its own entry action (run
            // before HandleEnterActiveFromStart, same ordering StageBehavior's D6 fix uses:
            // criteria armed before the cascade that follows) rather than folded into
            // HandleEnterActiveFromStart, since that method is shared verbatim with
            // StageBehavior's own Start/ManualStart arrival, which must not re-arm exit criteria
            // that HandleEnterAvailableFromCreate already armed for it.
            StateMachine.Configure(PlanItemState.Active)
                .OnEntryFromAsync(PlanItemTransition.Create, HandleEnterActiveFromCreate)
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
        //
        // #198 (should-fix, review round 2) - this override does NOT call base, so
        // StageBehavior.ManualCompletionCriteriaSatisfied's own AnyOutstandingRepetitionVerdicts
        // guard does not apply here automatically; it needs the identical check independently -
        // an external Trigger(Complete) on the CasePlanModel itself can race a direct child's
        // outstanding repetition verdict exactly the same way an ordinary Stage's manual
        // completion can (see the base method's own remarks for the full rationale).
        protected override async Task<bool> ManualCompletionCriteriaSatisfied()
        {
            if (StageStore.AnyOutstandingRepetitionVerdicts) return false;

            var childSnapshots = await GetChildSnapshots();

            return childSnapshots.All(x => x.PlanItemState != PlanItemState.Active) &&
                   childSnapshots.Where(x => x.Required).All(x => x.PlanItemState.IsTerminal());
        }
    }
}
