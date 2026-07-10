using System;
using Orleans;

namespace Flow.Grains.Plan.Sentry.Events
{
    // D11 - see SentryStore.Apply(OnPartNotRearmed) for why this clears only the one OnPart that
    // just completed the AND-join (superseding PR !18's IfPartNotSatisfied, which cleared every
    // recorded OnPart and was scoped to single-OnPart sentries only).
    [GenerateSerializer]
    public class OnPartNotRearmed
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        [Id(1)]
        public string OnPartId { get; set; }
    }
}
