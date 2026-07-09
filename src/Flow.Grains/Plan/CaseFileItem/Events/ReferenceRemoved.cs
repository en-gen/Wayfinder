using System;
using Orleans;

namespace Flow.Grains.Plan.CaseFileItem.Events
{
    // 8.3 - CaseFileItem Lifecycle, Table 8.2
    // ~~~~~
    // remove reference: Available -> Available. Another CaseFileItem instance is removed from the
    // target reference relationship (5.3.2's CaseFileItem.targetRefs).
    [GenerateSerializer]
    public class ReferenceRemoved
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        [Id(1)]
        public string TargetCaseFileItemId { get; set; }
    }
}
