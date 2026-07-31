using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    // #198 - the resolution half of RepetitionPending: raised once a Stage/CasePlanModel has
    // definitively handled the PlanItemRepetitionCriteriaMetEvent for SourceInstanceId, whichever
    // way it resolved - a fresh instance was actually spawned (StageBehavior.
    // SpawnRepetitionOrRefuseCeiling's success path), the #67 repetition ceiling refused it (same
    // method's breach path), or this container refused it outright because it is terminal or
    // Failed (HandleChildRepeated's #178 state switch). Removes the matching entry from
    // StageBehaviorStore's outstanding-verdict set AND records a permanent settlement tombstone,
    // letting a deferred Table 8.12 completion check (StageBehavior.TryCompleteStage) run again.
    //
    // Review round 2 - this event routinely arrives BEFORE the corresponding RepetitionPending,
    // not after: PlanItemRepetitionCriteriaMetEvent and PlanItemTransitionedEvent travel on
    // separate, unordered streams (the same root cause #198 exists to close), and the repetition-
    // met event's own delivery is frequently the FASTER of the two. Apply(RepetitionResolved) must
    // therefore settle unconditionally (not merely remove from the outstanding set), so a
    // RepetitionPending arriving later finds the tombstone and correctly no-ops instead of
    // stranding - see StageBehaviorStore's own remarks for the full commutative design.
    //
    // Deliberately NOT raised while a request is merely buffered awaiting a genuinely Suspended
    // container's resume (HandleChildRepeated's Suspended branch/BufferPendingRepetition): that
    // request is still real and unresolved, exactly like RepetitionBuffered's own preserve-not-
    // discard semantics - it resolves later, when DrainPendingRepetitions actually spawns or
    // ceiling-refuses it.
    [GenerateSerializer]
    public class RepetitionResolved : BaseUpdate
    {
        [Id(0)]
        public string SourceInstanceId { get; set; }
    }
}
