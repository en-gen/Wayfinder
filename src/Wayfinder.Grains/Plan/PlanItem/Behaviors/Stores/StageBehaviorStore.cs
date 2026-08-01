using System;
using System.Collections.Generic;
using System.Linq;
using Wayfinder.Grains.Plan.PlanItem.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Behaviors.Stores
{
    [GenerateSerializer]
    public class StageBehaviorStore
    {
        // only applicable to PlanItems defined by a Stage
        // PlanItemId => PlanItemInstanceId => Repetition
        [Id(0)]
        public IDictionary<string, IDictionary<string, int>> Children { get; } = new Dictionary<string, IDictionary<string, int>>();

        // #161 - durable redelivery guard, ported from SentryStore's OccurrenceToken/
        // IsRedelivery pattern rather than inventing a new mechanism. Keyed by the SOURCE child
        // PlanItem instance id that requested the repetition (ChildRepeated.SourceInstanceId, see
        // StageBehavior.HandleChildRepeated) - NOT by the newly-created repetition child's own
        // instance id (that one is always freshly minted per attempt, by design, and would never
        // collide). A given source instance can request exactly ONE repetition in its lifetime:
        // either via the entry-criterion OnPart path (HandleSentrySatisfied), which unsubscribes
        // from EntryCriteria immediately after publishing, or via the no-entry-criteria Complete/
        // Terminate path (BaseBehavior.TryRepeatOnCompleteOrTerminate), reachable only once
        // because CMMN terminal states have no further outgoing transition. So a second delivery
        // carrying the identical SourceInstanceId is necessarily the SAME logical request
        // redelivered - never a distinct, legitimate new repetition (which would arrive from a
        // DIFFERENT, freshly-minted child instance id - see CreateChild). Durable across
        // deactivation because this is populated via Apply(ChildRepeated), persisted by the same
        // ConfirmEvents() call that now (#160) confirms the rest of the repetition turn.
        //
        // Grows by one entry per repetition ever created for this host, unbounded - same growth
        // shape as Children above (itself already unbounded, in practice capped by
        // RepetitionGuardOptions.MaxRepetitionsPerPlanItem), accepted for the same reason: pruning
        // would reopen exactly the redelivery window this guard exists to close for any entry
        // pruned before a late redelivery of its event finally arrives.
        //
        // A repetition already in flight when this field is introduced (i.e. a ChildRepeated
        // raised by an older binary that never set SourceInstanceId) replays as a null entry on
        // upgrade, and IsRepetitionRedelivery below treats null as "no id supplied" (never a
        // match) - so a stream redelivery whose original delivery straddles this upgrade is not
        // caught by this guard. Acceptable: it is the same gap that existed for every prior
        // delivery before this fix shipped, not a regression, and it can only affect a delivery
        // in flight at the moment of upgrade, not steady-state operation.
        [Id(1)]
        private readonly ICollection<string> _repetitionSourceInstanceIds = new HashSet<string>();

        public bool IsRepetitionRedelivery(string sourceInstanceId) =>
            sourceInstanceId != null && _repetitionSourceInstanceIds.Contains(sourceInstanceId);

        // #198 - "this container is no longer holding Table 8.12 completion for that child's
        // repetition request", the third conjunct of StageBehavior.
        // RepetitionRequestsAwaitingResolution's blocking predicate - see that method for the full
        // design argument, including why this has to be a journaled record rather than something
        // derived from Children plus the configured ceiling.
        //
        // Keyed on the same requesting-child instance id as _repetitionSourceInstanceIds above, and
        // monotonic for the same reason (a source instance requests exactly one repetition in its
        // lifetime), but a SEPARATE set - not an alias of the #161 redelivery guard - on purpose:
        // that guard means specifically "a child HAS ALREADY BEEN SPAWNED for this request" and
        // StageBehavior.HandleChildRepeated returns early on it, so folding the refusal branches
        // into it would silently convert their documented redelivery behaviour (re-log the refusal;
        // re-raise RepetitionCeilingExceeded and re-attempt Fault - see
        // SpawnRepetitionOrRefuseCeiling's own #161 scope note) into a no-op.
        //
        // Populated from TWO parent-LOCAL sources, never from anything that travels on a stream:
        // Apply(ChildRepeated) (spawned - the successor exists) and Apply(RepetitionRequestSettled)
        // (refused, or stranded by a mid-drain ceiling fault). That asymmetry is the whole design of
        // the #198 fix: the BLOCKING signal is the child's own live, durable state (fetched by
        // direct grain call in StageBehavior.GetChildInstances, so it can never be lost or
        // reordered), while the CLEARING signal is produced by this container itself, locally, and
        // so can never race against it.
        //
        // Same unbounded growth as _repetitionSourceInstanceIds/Children, accepted for the same
        // reason: pruning an entry would let its child start blocking completion again forever.
        [Id(3)]
        private readonly ICollection<string> _settledRepetitionSourceInstanceIds = new HashSet<string>();

        public bool IsRepetitionSettled(string sourceInstanceId) =>
            sourceInstanceId != null && _settledRepetitionSourceInstanceIds.Contains(sourceInstanceId);

        // #198 - see IsRepetitionSettled above. Journaled by every refusing branch of
        // StageBehavior.HandleChildRepeated / SpawnRepetitionOrRefuseCeiling / the
        // DrainPendingRepetitions unknown-child drop, and by SettleStrandedPendingRepetitions; NOT
        // by the Suspended-buffered branch, whose request is still genuinely pending and MUST keep
        // blocking completion until the drain resolves it.
        public void Apply(RepetitionRequestSettled @event)
        {
            if (@event.SourceInstanceId != null)
            {
                _settledRepetitionSourceInstanceIds.Add(@event.SourceInstanceId);
            }
        }

        // #178 - repetition requests observed while this Host was genuinely Suspended, queued for
        // replay once it returns to Active (StageBehavior.DrainPendingRepetitions). A List (not a
        // Dictionary) so drain order is FIFO by arrival, matching the order the corresponding
        // PlanItemRepetitionCriteriaMetEvent deliveries actually occurred in; HasPendingRepetition
        // below is the dedupe check that keeps this keyed on SourceInstanceId in practice (the
        // #161 redelivery guard's own key) without needing a second index - the list is expected
        // to stay small (bounded by legitimate activity during one suspension), unlike Children/
        // _repetitionSourceInstanceIds above which grow for the container's entire lifetime.
        [Id(2)]
        public IList<PendingRepetition> PendingRepetitions { get; } = new List<PendingRepetition>();

        // #178 hazard 1 - keyed on the SAME SourceInstanceId the #161 guard above uses, so a
        // redelivered PlanItemRepetitionCriteriaMetEvent that arrives a second time while still
        // Suspended cannot queue a second, duplicate buffered entry for the same logical request.
        public bool HasPendingRepetition(string sourceInstanceId) =>
            sourceInstanceId != null && PendingRepetitions.Any(p => p.SourceInstanceId == sourceInstanceId);

        // #178 - write side of the buffer. A redelivery of an already-buffered request (same
        // SourceInstanceId) is intentionally a silent no-op here - StageBehavior's caller already
        // logs the redelivery before ever raising this event, so guarding it a second time here
        // would just be defensive-in-depth against a directly-constructed duplicate event.
        public void Apply(RepetitionBuffered @event)
        {
            if (@event.SourceInstanceId != null && !HasPendingRepetition(@event.SourceInstanceId))
            {
                PendingRepetitions.Add(new PendingRepetition
                {
                    SourceInstanceId = @event.SourceInstanceId,
                    PlanItemDefinitionId = @event.PlanItemDefinitionId,
                    NextRepetition = @event.NextRepetition
                });
            }
        }

        // #178 - removes one drained entry (whether it was successfully spawned or refused by the
        // #67 ceiling - see StageBehavior.DrainPendingRepetitions). SourceInstanceId is unique per
        // entry (HasPendingRepetition/Apply(RepetitionBuffered) above enforce that), so at most one
        // match is ever removed.
        public void Apply(RepetitionBufferDrained @event)
        {
            var entry = PendingRepetitions.FirstOrDefault(p => p.SourceInstanceId == @event.SourceInstanceId);
            if (entry != null)
            {
                PendingRepetitions.Remove(entry);
            }
        }

        public void Apply(ChildCreated @event)
        {
            if (!Children.TryGetValue(@event.PlanItemId, out var instances))
            {
                instances = new Dictionary<string, int>();
                Children[@event.PlanItemId] = instances;
            }

            instances[@event.PlanItemInstanceId] = @event.Repetition;
        }

        // Updated is intentionally NOT tracked here - same as Apply(ChildCreated) above, the
        // Updated stamp is the OUTER store's job (PlanItemStore/CaseStore.Apply(ChildRepeated)
        // sets it before delegating here), matching the existing ChildCreated delegation pattern
        // exactly.
        public void Apply(ChildRepeated @event)
        {
            if (@event.SourceInstanceId != null)
            {
                _repetitionSourceInstanceIds.Add(@event.SourceInstanceId);

                // #198 - a spawned successor settles the request too, and needs no event of its
                // own: this is the ONE branch that already journals a durable, confirmed record
                // (this event) at exactly the right moment. Recorded into the separate settled set
                // rather than having IsRepetitionSettled read the #161 set as well, so the two
                // meanings stay distinguishable in code and only THIS method ever writes both.
                _settledRepetitionSourceInstanceIds.Add(@event.SourceInstanceId);
            }
        }
    }
}
