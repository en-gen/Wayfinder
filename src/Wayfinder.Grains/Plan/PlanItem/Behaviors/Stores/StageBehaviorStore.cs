using System;
using System.Collections.Generic;
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
    }
}
