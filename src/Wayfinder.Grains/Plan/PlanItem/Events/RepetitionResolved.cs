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
    // StageBehaviorStore's pending-verdict set, letting a deferred Table 8.12 completion check
    // (StageBehavior.EvaluateStageCompletionCriteria) run again.
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
