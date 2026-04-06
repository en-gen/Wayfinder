using System;

namespace Flow.Grains.Interfaces.Plan.PlanItem.Behaviors
{
    [Serializable]
    public class Iso8601Snapshot
    {
        public string RawValue { get; set; }

        public bool HasRepetitions { get; set; }
        public int? Repetitions { get; set; }

        public DateTime? Start { get; set; }
        public DateTime? End { get; set; }
        public TimeSpan? Duration { get; set; }
    }
}
