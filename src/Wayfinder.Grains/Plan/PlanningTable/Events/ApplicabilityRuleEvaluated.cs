using Wayfinder.Grains.Plan.PlanItem.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanningTable.Events
{
    // #184 - intentionally never RaiseEvent'd as of this fix. PlanningTableGrain.
    // EvaluateApplicabilityRule (the only place that ever raised this) is reached solely from the
    // read-only GetPlannableItems() query, and a read must not journal - the prior behaviour raised
    // one of these per rule per call with no ConfirmEvents() anywhere on that path, so journal size
    // scaled 1:1 with read/poll volume rather than case activity.
    //
    // Kept (not deleted) so any journal persisted before this fix - which may still contain
    // confirmed instances of this event - continues to deserialize and replay correctly; Orleans
    // JournaledGrain replay requires every historical event type to remain resolvable. Do not
    // re-raise this from a query/read path. Do not delete this type without first confirming no
    // pre-#184 journal (any environment, including at-rest backups) can still contain it.
    [GenerateSerializer]
    public class ApplicabilityRuleEvaluated : RuleEvaluated<bool>
    {
    }
}
