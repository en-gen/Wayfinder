using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    // ADO #67 - Wayfinder ENGINE EXTENSION, NOT CMMN 1.1 spec surface.
    // ~~~~~
    // Raised on the Stage/CasePlanModel instance (Host, in StageBehavior.HandleChildRepeated)
    // that refused to spawn one more repetition of RepeatingPlanItemDefinitionId because
    // AttemptedRepetition would meet or exceed RepetitionGuardOptions.MaxRepetitionsPerPlanItem
    // (Ceiling). Immediately followed by driving that same Stage/CasePlanModel to Fault - see
    // RepetitionGuardOptions' remarks for why the CONTAINER faults rather than the repeating
    // item itself (the item that requested the repeat is either already terminal or still
    // legitimately running, neither a legal Fault target).
    //
    // Do not treat this event, or a case that raises it, as a CMMN conformance failure: 8.6.4
    // imposes no repetition limit, so this is a deliberate engine safety valve catching a
    // modeler mistake (see the #19 foot-gun), not spec behavior and not an engine defect.
    [GenerateSerializer]
    public class RepetitionCeilingExceeded : BaseUpdate
    {
        [Id(1)]
        public string RepeatingPlanItemDefinitionId { get; set; }
        [Id(2)]
        public int AttemptedRepetition { get; set; }
        [Id(3)]
        public int Ceiling { get; set; }
    }
}
