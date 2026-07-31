using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    // #198 (review round 2) - raised on entry to Completed, Terminated, or Failed (StageBehavior's
    // constructor wires this to all three - see its remarks) to drop every currently-outstanding
    // repetition-verdict mark for this container: StageBehaviorStore.
    // ClearOutstandingRepetitionVerdicts' own remarks explain why this is needed (closing both a
    // dead-state leak on ordinary terminal entry and a genuine Table-8.12-completion-blocking
    // strand when StageBehavior.DrainPendingRepetitions hits the #67 ceiling mid-batch). No
    // payload - it is a blanket clear, not keyed to one source instance id, since every mark
    // outstanding at that moment shares the same fate.
    [GenerateSerializer]
    public class OutstandingRepetitionVerdictsCleared : BaseUpdate
    {
    }
}
