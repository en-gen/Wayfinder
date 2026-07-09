using System;
using System.Collections.Generic;
using Flow.Grains.Plan.CmmnElement;
using Flow.Grains.Plan.Sentry.Events;
using Orleans;

namespace Flow.Grains.Plan.Sentry
{
    [GenerateSerializer]
    public class SentryStore : CmmnElementStore<Interfaces.Model.Sentry>
    {
        // Tracked by OnPart.Id (string) rather than the OnPart object itself. Note: CmmnElement
        // (OnPart's base) already overrides Equals/GetHashCode by Id
        // (Flow.Grains.Interfaces/Model/CmmnElement.cs), so a HashSet<OnPart> was not actually
        // vulnerable to reference-equality duplication here - verified directly, two distinct
        // OnPart instances sharing the same Id already collapse to one HashSet<OnPart> entry.
        // Tracking by the Id string explicitly is still preferable: it avoids relying on the
        // referenced model type's Equals doing the right thing, and it keeps the persisted store
        // from holding full OnPart object graphs (with their own nested collections) just to record
        // that an occurrence happened.
        [Id(0)]
        private readonly ICollection<string> _occurredOnPartIds = new HashSet<string>();
        public IEnumerable<string> OccurredOnPartIds => _occurredOnPartIds;
        [Id(1)]
        public bool Satisfied { get; private set; }

        public void Apply(OnPartOccurred @event)
        {
            _occurredOnPartIds.Add(@event.OnPart.Id);

            Updated = @event.Updated;
        }

        public void Apply(Satisfied @event)
        {
            Updated = @event.Updated;
            Satisfied = true;
        }

        // 8.5 - Sentry
        // ~~~~~
        // All OnParts had occurred (the AND-join was complete) when this was raised, but the IfPart
        // evaluated false, so the sentry as a whole is not satisfied. Clearing OccurredOnPartIds
        // here - rather than leaving them recorded forever - is what lets a SUBSEQUENT occurrence of
        // an OnPart (e.g. a second CaseFileItemTransition.Update on the same CaseFileItem) retrigger
        // the AND-join and IfPart re-check per 8.5's "must re-evaluate on subsequent relevant events."
        // Without this, HandleOnPartOccurred's redelivery guard (OccurredOnPartIds.Contains(onPart.Id))
        // - which exists to make true at-least-once stream redelivery of the *same* logical transition
        // a no-op - would also permanently swallow every later, genuinely distinct occurrence of that
        // OnPart, since OnPart.Id is the fixed model-element id, not a per-occurrence identifier.
        public void Apply(IfPartNotSatisfied @event)
        {
            Updated = @event.Updated;
            _occurredOnPartIds.Clear();
        }
    }
}
