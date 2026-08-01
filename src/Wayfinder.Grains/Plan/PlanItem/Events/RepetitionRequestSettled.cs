using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    // #198 - this Stage/CasePlanModel is no longer holding Table 8.12 completion for one child's
    // repetition request, without having spawned a successor for it. Raised by StageBehavior on the
    // refusing branches (the #67 ceiling; #178's terminal-container and Failed-container refusals;
    // an unrecognized child) and by SettleStrandedPendingRepetitions; deliberately NOT raised on
    // #178's Suspended branch, where the request is buffered and genuinely still pending until
    // DrainPendingRepetitions later spawns or ceiling-refuses it.
    //
    // Note the deliberately weak wording. On every branch but one, no successor is ever coming. The
    // exception is SettleStrandedPendingRepetitions: an entry stranded behind a mid-drain ceiling
    // fault stays buffered and could still be spawned by some later Suspend/Resume drain - it is
    // settled only so it cannot wedge Complete on a reactivated container (see that method). If it
    // does spawn, ChildRepeated settles it again into the same monotonic set, so nothing
    // contradicts.
    //
    // Purpose, and why the parent journals a refusal it previously only logged: #198's blocking
    // predicate (StageBehavior.RepetitionRequestsAwaitingResolution) holds up Table 8.12
    // completion for any terminal child whose own live state says `Repeated` while this container
    // has not yet acted on that declaration. Without a durable record, a refused request would
    // block completion forever - the escape hatch, not an afterthought. That method also carries
    // the argument for why this record is journaled rather than derived from state already at hand.
    // The SPAWNED case needs no event of its own: ChildRepeated (#161) is already raised and
    // confirmed on that path, and StageBehaviorStore.Apply(ChildRepeated) settles from it.
    //
    // SourceInstanceId is the requesting child instance's own id (PlanItemRepetitionCriteriaMetEvent.
    // PlanItemInstanceId) - the same idempotency key ChildRepeated/RepetitionBuffered carry; see
    // StageBehaviorStore.IsRepetitionRedelivery for why one source instance maps to exactly one
    // logical repetition request for its whole lifetime.
    //
    // Reason is audit detail only - nothing branches on it. It exists so the journal (and any
    // future replay/inspection) records WHICH branch settled the request, since those branches are
    // semantically different even though their effect on the predicate is identical.
    [GenerateSerializer]
    public class RepetitionRequestSettled : BaseUpdate
    {
        [Id(0)]
        public string SourceInstanceId { get; set; }
        [Id(1)]
        public string Reason { get; set; }
    }
}
