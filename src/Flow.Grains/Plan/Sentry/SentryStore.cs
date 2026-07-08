using System;
using System.Collections.Generic;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.CmmnElement;
using Flow.Grains.Plan.Sentry.Events;
using Orleans;

namespace Flow.Grains.Plan.Sentry
{
    [GenerateSerializer]
    public class SentryStore : CmmnElementStore<Interfaces.Model.Sentry>
    {
        [Id(0)]
        private readonly ICollection<OnPart> _occurredOnParts = new HashSet<OnPart>();
        public IEnumerable<OnPart> OccurredOnParts => _occurredOnParts;
        [Id(1)]
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
