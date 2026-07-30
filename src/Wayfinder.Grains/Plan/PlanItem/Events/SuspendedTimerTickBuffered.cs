using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    // #182 (sub-claim 2) - "ticks arriving while Suspended are dropped."
    // ~~~~~
    // Raised by TimerEventListenerBehavior.ProcessTick when a TimerTickedEvent arrives while
    // Host.State.PlanItemState is Suspended. Docs section 2 (03-cmmn-execution-semantics.md):
    // suspension preserves state rather than discarding it, and no repetition/occurrence trigger
    // can legitimately originate inside a genuinely Suspended item - so a tick observed while
    // Suspended was earned before suspension and is merely late in delivery. Durably recording it
    // here (rather than silently no-op'ing, the pre-fix behavior) is what lets
    // TimerEventListenerBehavior.HandleEnterAvailableFromResume replay it once the listener
    // actually returns to Available - without this, a one-shot timer's single Quartz trigger is
    // spent forever and the listener can never reach Completed.
    [GenerateSerializer]
    public class SuspendedTimerTickBuffered : BaseUpdate
    {
        [Id(1)]
        public DateTimeOffset FireTime { get; set; }
    }
}
