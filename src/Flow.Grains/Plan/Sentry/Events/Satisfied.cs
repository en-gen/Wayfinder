using System;
using Orleans;

namespace Flow.Grains.Plan.Sentry.Events
{
    [GenerateSerializer]
    public class Satisfied
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;
    }
}