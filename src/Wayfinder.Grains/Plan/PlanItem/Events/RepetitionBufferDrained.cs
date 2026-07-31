using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    // #178 - removes a single entry from StageBehaviorStore.PendingRepetitions, keyed on the same
    // SourceInstanceId RepetitionBuffered recorded it under. Raised once per buffered entry as
    // StageBehavior.DrainPendingRepetitions works through the queue on Resume/ParentResume -
    // whether that entry was actually spawned or refused by the #67 repetition ceiling (a
    // ceiling-refused buffered request is not retried, matching the live ceiling path's own
    // no-retry semantics - see DrainPendingRepetitions' remarks).
    [GenerateSerializer]
    public class RepetitionBufferDrained : BaseUpdate
    {
        [Id(0)]
        public string SourceInstanceId { get; set; }
    }
}
