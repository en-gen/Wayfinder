using System;
using Orleans;

namespace Flow.Grains.Plan.CaseFileItem.Events
{
    // 8.3 - CaseFileItem Lifecycle, Table 8.2
    // ~~~~~
    // delete: Available -> Discarded. Terminal state.
    [GenerateSerializer]
    public class Discarded
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;
    }
}
