using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    // #178 - Failed is semi-terminal and re-activatable (Reactivate -> Active, ConfigureForStageOrTask
    // /ConfigureForCasePlanModel), so unlike Completed/Terminated/Closed it is not unambiguously
    // "done" - but re-activation is a human recovery action, and silently replaying a stale
    // repetition request into a just-recovered case would be surprising, not helpful. So this
    // container REFUSES the spawn, same as the terminal-state branch, but - unlike that branch -
    // raises this loud, observable event rather than only a log line, so the refusal is visible to
    // whoever investigates the Failed case (mirrors RepetitionCeilingExceeded's own
    // audit-event-not-state-mutation shape: no store Apply() overload exists for this event
    // either, deliberately - see that type's remarks).
    [GenerateSerializer]
    public class RepetitionRefusedWhileFailed : BaseUpdate
    {
        // The repeating child PlanItem's OWN Id (the <planItem> element's id), NOT its
        // DefinitionRef/PlanItemDefinition.Id - see StageBehavior.HandleChildRepeated, which
        // populates this from child.Id. Property name kept aligned with that semantics (renamed
        // from the misleading RepeatingPlanItemDefinitionId, mirroring the #132 fix on
        // RepetitionCeilingExceeded.RepeatingPlanItemId); [Id(0)] is unchanged for wire/replay
        // compatibility.
        [Id(0)]
        public string RepeatingPlanItemId { get; set; }
        [Id(1)]
        public string SourceInstanceId { get; set; }
        [Id(2)]
        public int AttemptedRepetition { get; set; }
    }
}
