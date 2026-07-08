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
    }
}
