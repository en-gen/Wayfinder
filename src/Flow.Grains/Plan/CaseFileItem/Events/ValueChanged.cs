using System;
using System.Text.Json.Nodes;
using Orleans;

namespace Flow.Grains.Plan.CaseFileItem.Events
{
    // Journaled for the update/replace/addChild/removeChild/addReference/removeReference
    // self-transitions (Table 8.2) - all of them mutate CaseFileItem content and are otherwise
    // identical from the store's point of view (the distinct standardEvent each one publishes
    // is what SentryGrain/TimerEventListenerBehavior key on; the store only needs the new Value).
    [GenerateSerializer]
    public class ValueChanged
    {
        [Id(0)]
        public DateTime Updated { get; } = DateTime.UtcNow;

        [Id(1)]
        public JsonNode Value { get; set; }
    }
}
