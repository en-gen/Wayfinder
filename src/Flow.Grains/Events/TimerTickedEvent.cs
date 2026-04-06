using System;

namespace Flow.Grains.Events
{
    [Serializable]
    public class TimerTickedEvent
    {
        public DateTimeOffset? PreviousFireTime { get; }
        public DateTimeOffset? ScheduledFireTime { get; }
        public DateTimeOffset FireTime { get; }
        public DateTimeOffset? NextFireTime { get; }

        public TimerTickedEvent(
            DateTimeOffset? previousFireTime,
            DateTimeOffset? scheduledFireTime,
            DateTimeOffset fireTime,
            DateTimeOffset? nextFireTime)
        {
            PreviousFireTime = previousFireTime;
            ScheduledFireTime = scheduledFireTime;
            FireTime = fireTime;
            NextFireTime = nextFireTime;
        }
    }
}
