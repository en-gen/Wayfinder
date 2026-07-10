using System;
using Orleans;

namespace Flow.Grains.Plan.Sentry.Events
{
    // D10 - see SentryGrain.HandleOnPartOccurred's remarks for when this is raised (an IfPart that
    // could not be evaluated at all, distinct from one that evaluated cleanly to FALSE).
    [GenerateSerializer]
    public class Faulted
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;
    }
}
