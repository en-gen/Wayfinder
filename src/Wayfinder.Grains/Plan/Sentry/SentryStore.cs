using System;
using System.Collections.Generic;
using Wayfinder.Grains.Plan.CmmnElement;
using Wayfinder.Grains.Plan.Sentry.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.Sentry
{
    // D5/D11 - Sentry re-arm semantics (supersedes PR !18's single-OnPart-only,
    // clears-everything IfPartNotSatisfied; see SentryGrain.HandleOnPartOccurred's remarks).
    // ~~~~~
    // 8.5's AND-join and 8.6.4's repetition text ("every time an entry criterion with an OnPart is
    // satisfied... a new instance... is created") both presuppose a Sentry, and each of its OnParts,
    // can occur/satisfy more than once over the Sentry's life - once per repetition of whatever the
    // OnPart's source itself repeats as (Figure 8.6's B/B'/B", each a physically distinct PlanItem
    // instance independently reaching the watched standardEvent), or once per distinct relevant
    // CaseFileItem event. A bare "has this OnPart.Id occurred" flag cannot tell a GENUINELY NEW
    // occurrence (which per 8.5/8.6.4 MUST re-trigger the AND-join/IfPart check) apart from a true
    // at-least-once stream REDELIVERY of the exact same logical transition (which must stay a
    // no-op - see HandlePlanItemTransitioned__Given_SameEventDeliveredTwice__Then_
    // PublishSentrySatisfiedEventExactlyOnce).
    //
    // For a PlanItemOnPart, PlanItemTransitionedEvent.SourceInstanceId (the specific PlanItem
    // GRAIN's own instance id - see PlanItemGrain/BaseBehavior.HandleTransitioned) is exactly this
    // distinguishing signal: StageBehavior.CreateChild assigns each repetition a freshly generated,
    // distinct instance id, so B and B' genuinely differ here even though they share the same
    // PlanItem.Id/DefinitionRef (which is what OnPart.SourceRef matches on and is, correctly,
    // IDENTICAL across repetitions - see Table 5.30). A redelivery of the exact same transition
    // necessarily carries the identical SourceInstanceId (there is only one PlanItem grain instance
    // to redeliver from); a later, distinct repetition's transition necessarily carries a different
    // one.
    //
    // CaseFileItemOnPart has no equivalent concept (a CaseFileItem is not re-instantiated the way a
    // PlanItem is - one CaseFileItemGrain persists for a given id across every Create/Update/etc. -
    // see CaseFileItemGrain), so its occurrences carry no OccurrenceToken (null) and fall back to
    // the existing per-OnPart latch-until-explicitly-cleared behavior (Apply(OnPartNotRearmed)),
    // which the D1 flagship test (CaseFileItemSentryIntegrationTests.CaseFileItemUpdate__Given_
    // SentryWithCaseFileItemOnPartAndIfPart__Then_SentryOnlyFiresWhenConditionTrue) already proves
    // correct for repeated, genuinely distinct Update() calls on the same CaseFileItem.
    [GenerateSerializer]
    public class SentryStore : CmmnElementStore<Interfaces.Model.Sentry>
    {
        // Keyed by OnPart.Id (the fixed model-element id, not a per-occurrence identifier), valued
        // by the OccurrenceToken of the latest occurrence recorded for that OnPart (null for a
        // CaseFileItemOnPart occurrence, or one with no distinguishing token supplied).
        [Id(0)]
        private readonly IDictionary<string, string> _onPartOccurrenceTokens = new Dictionary<string, string>();
        public IEnumerable<string> OccurredOnPartIds => _onPartOccurrenceTokens.Keys;
        [Id(1)]
        public bool Satisfied { get; private set; }
        // D10 - true once an IfPart evaluation has faulted (as opposed to evaluating cleanly to
        // FALSE). See SentryGrain.HandleOnPartOccurred/EvaluateIfPart's IfPartResult.
        [Id(2)]
        public bool Faulted { get; private set; }

        // True when this exact occurrence (this OnPart.Id, at this OccurrenceToken) is already the
        // latest one recorded for that OnPart - i.e. a redelivery of the same logical transition,
        // not a new occurrence. A null occurrenceToken (CaseFileItemOnPart, or a PlanItemOnPart
        // occurrence with no SourceInstanceId available) is treated as "no distinguishing
        // information" and always counts as redelivery once ANY occurrence of that OnPart.Id is
        // recorded - preserving the original, still-correct behavior for that case (re-arming there
        // depends entirely on Apply(OnPartNotRearmed) clearing the entry explicitly, not on token
        // comparison).
        public bool IsRedelivery(string onPartId, string occurrenceToken) =>
            _onPartOccurrenceTokens.TryGetValue(onPartId, out var recorded) &&
            (occurrenceToken == null || recorded == occurrenceToken);

        public void Apply(OnPartOccurred @event)
        {
            _onPartOccurrenceTokens[@event.OnPart.Id] = @event.OccurrenceToken;

            Updated = @event.Updated;
        }

        public void Apply(Satisfied @event)
        {
            Updated = @event.Updated;
            Satisfied = true;
        }

        public void Apply(Faulted @event)
        {
            Updated = @event.Updated;
            Faulted = true;
        }

        // D11 (generalizes PR !18's single-OnPart-only IfPartNotSatisfied; see
        // SentryGrain.HandleOnPartOccurred's remarks for why a blanket clear does not generalize to
        // multi-OnPart sentries).
        // ~~~~~
        // 8.5: "a sentry whose OnParts have all occurred but whose IfPart is false does NOT fire...
        // and must re-evaluate on subsequent relevant events." Clearing ONLY the OnPart that just
        // completed the AND-join - not every recorded OnPart - is what makes this safe for 2+
        // OnParts: a sibling OnPart whose own source is inherently one-shot (e.g. a PlanItemTransition
        // that only ever fires once in that PlanItem's lifecycle) keeps its recorded occurrence, so
        // the AND-join remains reachable on the next occurrence of the OnPart that just re-armed,
        // rather than becoming permanently unreachable because a one-shot sibling's occurrence was
        // also forgotten.
        public void Apply(OnPartNotRearmed @event)
        {
            Updated = @event.Updated;
            _onPartOccurrenceTokens.Remove(@event.OnPartId);
        }
    }
}
