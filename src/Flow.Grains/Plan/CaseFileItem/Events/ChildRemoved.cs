using System;
using Orleans;

namespace Flow.Grains.Plan.CaseFileItem.Events
{
    // 8.3 - CaseFileItem Lifecycle, Table 8.2
    // ~~~~~
    // remove child: Available -> Available. Another CaseFileItem instance is removed from the
    // children relationship (5.3.2's CaseFileItem.children).
    [GenerateSerializer]
    public class ChildRemoved
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        [Id(1)]
        public string ChildCaseFileItemId { get; set; }
    }
}
