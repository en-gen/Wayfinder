using System;
using Orleans;

namespace Flow.Grains.Interfaces.Plan.PlanItem.Behaviors
{
    [GenerateSerializer]
    public class Iso8601Snapshot
    {
        [Id(0)]
        public string RawValue { get; set; }

        [Id(1)]
        public bool HasRepetitions { get; set; }
        [Id(2)]
        public int? Repetitions { get; set; }

        [Id(3)]
        public DateTime? Start { get; set; }
        [Id(4)]
        public DateTime? End { get; set; }
        [Id(5)]
        public TimeSpan? Duration { get; set; }
    }
}
