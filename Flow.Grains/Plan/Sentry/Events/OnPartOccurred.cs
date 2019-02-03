using System;
using Flow.Grains.Interfaces.Model;

namespace Flow.Grains.Plan.Sentry.Events
{
    [Serializable]
    public class OnPartOccurred
    {
        public DateTime Updated { get; } = DateTime.UtcNow;

        public OnPart OnPart { get; set; }
    }
}