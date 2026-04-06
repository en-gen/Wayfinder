using System;
using System.Collections.Generic;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.CmmnElement;
using Flow.Grains.Plan.Sentry.Events;

namespace Flow.Grains.Plan.Sentry
{
    [Serializable]
    public class SentryStore : CmmnElementStore<Interfaces.Model.Sentry>
    {
        private readonly ICollection<OnPart> _occurredOnParts = new HashSet<OnPart>();
        public IEnumerable<OnPart> OccurredOnParts => _occurredOnParts;
        public bool Satisfied { get; private set; }

        public void Apply(OnPartOccurred @event)
        {
            _occurredOnParts.Add(@event.OnPart);

            Updated = @event.Updated;
        }

        public void Apply(Satisfied @event)
        {
            Updated = @event.Updated;
            Satisfied = true;
        }
    }
}
