using System;
using Orleans;

namespace Flow.Grains.Events
{
    [GenerateSerializer]
    public class TimerTickedEvent
    {
        [Id(0)]
        public DateTimeOffset? PreviousFireTime { get; }
        [Id(1)]
        public DateTimeOffset? ScheduledFireTime { get; }
        [Id(2)]
        public DateTimeOffset FireTime { get; }
        [Id(3)]
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
