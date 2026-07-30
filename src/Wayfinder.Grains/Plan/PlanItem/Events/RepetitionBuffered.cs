using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    // #178 - a repetition request that arrives while THIS Stage/CasePlanModel is genuinely
    // Suspended is buffered rather than spawned or dropped: Table 8.9 + Figure 8.3 (history
    // pseudo-state) make suspension preserve-and-restore, and Table 8.8's complete row is
    // Active->Completed only (8.5 evaluates entry criteria while Available) - so no repetition
    // trigger can legitimately ORIGINATE inside a genuinely Suspended Stage. A request observed
    // here was earned before suspension and is merely late (this engine's async transport), not a
    // modeled scenario - see StageBehavior.HandleChildRepeated's Suspended branch and
    // docs/03-cmmn-execution-semantics.md section 2.
    //
    // SourceInstanceId mirrors ChildRepeated's own field exactly, and for the same reason: it is
    // the requesting child's own instance id, durable and unique per legitimate repetition
    // request, used both to dedupe a redelivered buffering request (StageBehaviorStore.
    // HasPendingRepetition) and later to populate ChildRepeated.SourceInstanceId once the buffered
    // request is actually drained (see StageBehavior.DrainPendingRepetitions), so the #161
    // redelivery guard covers a drained-and-spawned child exactly as it would have covered one
    // spawned live.
    [GenerateSerializer]
    public class RepetitionBuffered : BaseUpdate
    {
        [Id(0)]
        public string SourceInstanceId { get; set; }
        // The requesting child's PlanItem.Id (NOT PlanItemDefinition.Id - same distinction
        // ChildCreated.PlanItemId already draws) - looked back up against
        // PlanItemDefinition.PlanItems on drain, exactly as HandleChildRepeated's live path looks
        // it up from @event.SourceDefinitionId.
        [Id(1)]
        public string PlanItemDefinitionId { get; set; }
        // The repetition index that would have been created had this Stage been Active when the
        // request arrived (@event.CurrentRepetition + 1, computed once at buffering time) -
        // persisted verbatim rather than recomputed on drain, since the source event that produced
        // it is not itself replayed.
        [Id(2)]
        public int NextRepetition { get; set; }
    }
}
