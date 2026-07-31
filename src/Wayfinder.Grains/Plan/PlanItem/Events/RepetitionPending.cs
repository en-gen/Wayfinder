using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    // #198 - raised by a Stage/CasePlanModel when a direct child's PlanItemTransitionedEvent
    // arrives with Destination.IsTerminal() and WillRepeat=true (BaseBehavior.
    // HandleTransitioned's #198 verdict - see its remarks). Records that a replacement instance
    // for SourceInstanceId is expected but does not yet durably exist, so
    // StageBehavior.EvaluateStageCompletionCriteria (Table 8.12) must not treat this child as
    // simply "terminal" - Table 8.9's complete rows mark a Stage/Task child in
    // Available/Enabled/Active/Suspended as <impossible> alongside a Completed parent, and this
    // forthcoming instance is exactly that child, just not created yet. Cleared by
    // RepetitionResolved once the corresponding PlanItemRepetitionCriteriaMetEvent is actually
    // handled (spawned, refused, or faulted) - see StageBehavior.ResolveRepetitionPending.
    //
    // SourceInstanceId mirrors ChildRepeated/RepetitionBuffered's own field, and for the same
    // reason: it is the requesting (terminal) child's own instance id, the correlation key a
    // later PlanItemRepetitionCriteriaMetEvent for the SAME logical request will carry as its own
    // PlanItemInstanceId.
    [GenerateSerializer]
    public class RepetitionPending : BaseUpdate
    {
        [Id(0)]
        public string SourceInstanceId { get; set; }
    }
}
