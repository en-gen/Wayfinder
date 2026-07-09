using System;
using Orleans;

namespace Flow.Grains.Plan.CaseFileItem.Events
{
    // 8.3 - CaseFileItem Lifecycle, Table 8.2
    // ~~~~~
    // add child: Available -> Available. Another CaseFileItem instance is added to the children
    // relationship (5.3.2's CaseFileItem.children).
    [GenerateSerializer]
    public class ChildAdded
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        [Id(1)]
        public string ChildCaseFileItemId { get; set; }
    }
}
