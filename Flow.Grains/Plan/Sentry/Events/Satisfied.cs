using System;

namespace Flow.Grains.Plan.Sentry.Events
{
    [Serializable]
    public class Satisfied
    {
        public DateTime Updated { get; } = DateTime.UtcNow;
    }
}