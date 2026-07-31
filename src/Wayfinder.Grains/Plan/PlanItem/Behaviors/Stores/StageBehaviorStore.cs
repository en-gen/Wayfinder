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
        // Terminate path (BaseBehavior.HandleTransitioned/EvaluateRepetitionOnTerminalTransition), reachable only once
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
            }
        }

        // #198 (review round 2 - the original single-set design assumed
        // PlanItemTransitionedEvent{WillRepeat=true} always reaches this Stage BEFORE the
        // corresponding PlanItemRepetitionCriteriaMetEvent. It does not: both travel on separate,
        // unordered streams - the SAME root cause #198 exists to close - so the repetition-met
        // event routinely arrives FIRST. A single "add on pending, remove on resolve" set is not
        // commutative under that reordering: if resolution is processed before the matching
        // "pending" mark ever arrives, Apply(RepetitionPending) would add an entry with nothing
        // left to ever remove it, wedging Table 8.12 for this container forever - exactly the
        // "worse than the bug" failure this mechanism exists to avoid.
        //
        // Fixed with TWO sets forming a commutative, order-independent verdict tracker per source
        // instance id:
        //   - _outstandingRepetitionVerdictSourceInstanceIds: a "pending" mark IS currently
        //     outstanding for this id - StageBehavior.TryCompleteStage (Table
        //     8.12) must not complete while this is non-empty (Table 8.9's complete rows mark a
        //     Stage/Task child in Available/Enabled/Active/Suspended as <impossible> alongside a
        //     Completed parent, and an entry here IS exactly that child, merely not yet durably
        //     created).
        //   - _settledRepetitionVerdictSourceInstanceIds: a PERMANENT tombstone - this id's
        //     repetition request has been definitively resolved (spawned, #67-ceiling-refused, or
        //     #178 terminal/Failed-refused), no matter which order the two events arrived in.
        //     Apply(RepetitionPending) below consults this BEFORE adding to the outstanding set:
        //     if resolution already happened, marking pending now would have nothing left to
        //     resolve it, so it is correctly a no-op instead. This is also what makes an
        //     at-least-once REDELIVERY of the terminal PlanItemTransitionedEvent itself safe: a
        //     redelivery arriving after its own resolution finds the tombstone and no-ops, rather
        //     than re-adding a mark that (its one resolving event already consumed) would never
        //     be removed again.
        // Never pruned, same rationale as _repetitionSourceInstanceIds' own accepted unbounded
        // growth above: pruning a tombstone would reopen exactly the reordering/redelivery window
        // it exists to close for a mark that arrives arbitrarily late.
        //
        // Enforced HERE, in the Apply methods, not merely as a pre-check in the StageBehavior
        // methods that raise these events: Apply is what a reactivated grain replays from the
        // journal, so this is the actual source of correctness, not an optimization.
        [Id(3)]
        private readonly ICollection<string> _outstandingRepetitionVerdictSourceInstanceIds = new HashSet<string>();

        [Id(4)]
        private readonly ICollection<string> _settledRepetitionVerdictSourceInstanceIds = new HashSet<string>();

        public bool HasOutstandingRepetitionVerdict(string sourceInstanceId) =>
            sourceInstanceId != null && _outstandingRepetitionVerdictSourceInstanceIds.Contains(sourceInstanceId);

        public bool AnyOutstandingRepetitionVerdicts => _outstandingRepetitionVerdictSourceInstanceIds.Count > 0;

        public bool IsRepetitionVerdictSettled(string sourceInstanceId) =>
            sourceInstanceId != null && _settledRepetitionVerdictSourceInstanceIds.Contains(sourceInstanceId);

        public void Apply(RepetitionPending @event)
        {
            if (@event.SourceInstanceId == null) return;

            // Commutative order (see the class remarks above): if this source instance's
            // repetition request was ALREADY settled - the resolving event arrived first, or
            // this is a stale redelivery of a PlanItemTransitionedEvent whose resolution already
            // happened - there is nothing left to wait for. Adding it now would strand it.
            if (_settledRepetitionVerdictSourceInstanceIds.Contains(@event.SourceInstanceId)) return;

            _outstandingRepetitionVerdictSourceInstanceIds.Add(@event.SourceInstanceId);
        }

        public void Apply(RepetitionResolved @event)
        {
            if (@event.SourceInstanceId == null) return;

            _outstandingRepetitionVerdictSourceInstanceIds.Remove(@event.SourceInstanceId);
            _settledRepetitionVerdictSourceInstanceIds.Add(@event.SourceInstanceId);
        }

        // #198 (should-fix, review round 2) - drops every currently-outstanding mark without
        // settling it, for use ONLY on entry to a state from which this container can never again
        // legitimately evaluate Table 8.12 (Completed, Terminated) or from which the #178
        // buffered-repetition drain is deliberately never auto-retried (Failed - see
        // StageBehavior's own remarks on why Reactivate does not re-run DrainPendingRepetitions).
        // Two concrete strandings this closes:
        //   1. A mark whose resolving event will genuinely never arrive once this container is
        //      terminal has no other way to be cleared - not a live bug (Table 8.12 never runs
        //      again for a terminal container either way), but leaves dead state sitting in the
        //      durable store forever otherwise.
        //   2. StageBehavior.DrainPendingRepetitions hitting the #67 ceiling mid-batch (Fault) or
        //      catching a transient CreateChild failure leaves the REMAINING un-drained buffer
        //      entries - and this store's marks for them - permanently stranded once this
        //      container reactivates from Failed (drain is not re-run on Reactivate, by design).
        //      Without this clear, THAT durable emptiness of the #178 buffer would silently
        //      become a durable, permanent block on Table 8.12 for the whole container - a much
        //      larger blast radius than #178's own accepted "those specific children never spawn"
        //      limitation.
        // Deliberately does NOT settle the cleared ids: once this container is terminal, nothing
        // ever consults _outstandingRepetitionVerdictSourceInstanceIds again, so there is nothing
        // for a settlement tombstone to protect here. A LEGITIMATE resolution for one of these ids
        // that is still in flight (e.g. a Failed-refuse for an unrelated, still-live repetition
        // request) settles itself normally via Apply(RepetitionResolved) regardless of whether
        // this method already cleared it - Remove/Add on a HashSet are idempotent no-ops when the
        // entry is already absent/present.
        //
        // Journaled like every other mutation here (via OutstandingRepetitionVerdictsCleared),
        // NOT a bare in-place Clear() a caller could invoke directly - this store is JournaledGrain
        // state, so any mutation not driven by an Apply(TEvent) would silently fail to replay on
        // reactivation (see StageBehavior.ClearOutstandingRepetitionVerdictsOnTerminalEntry, which
        // raises the event and confirms it).
        public void Apply(OutstandingRepetitionVerdictsCleared @event)
        {
            _outstandingRepetitionVerdictSourceInstanceIds.Clear();
        }
    }
}
