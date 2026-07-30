using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    // #182 (sub-claim 2) - pairs with SuspendedTimerTickBuffered.
    // ~~~~~
    // Raised once by TimerEventListenerBehavior.HandleEnterAvailableFromResume, before replaying
    // any buffered ticks, to durably clear TimerEventListenerBehaviorStore.PendingSuspendedTicks.
    // Cleared up front (against a locally captured snapshot of the pending list) rather than
    // per-item, so the replay loop below is never iterating a collection the same turn is also
    // mutating.
    [GenerateSerializer]
    public class SuspendedTimerTicksReplayed : BaseUpdate
    {
    }
}
