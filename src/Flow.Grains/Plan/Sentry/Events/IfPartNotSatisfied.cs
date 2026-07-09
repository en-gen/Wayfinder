using System;
using Orleans;

namespace Flow.Grains.Plan.Sentry.Events
{
    // 8.5 - Sentry
    // ~~~~~
    // "A sentry whose OnParts have all occurred but whose IfPart is false does NOT fire... and must
    // re-evaluate on subsequent relevant events." Raised when SentryGrain.HandleOnPartOccurred finds
    // all OnParts occurred (the AND-join is complete) but EvaluateIfPart() returns false - see
    // SentryStore.Apply(IfPartNotSatisfied) for why this resets OccurredOnPartIds rather than only
    // recording the attempt.
    [GenerateSerializer]
    public class IfPartNotSatisfied
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;
    }
}
