using System;
using Flow.Grains.Interfaces.Model;
using Orleans;

namespace Flow.Grains.Plan.Sentry.Events
{
    [GenerateSerializer]
    public class OnPartOccurred
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        [Id(1)]
        public OnPart OnPart { get; set; }
    }
}