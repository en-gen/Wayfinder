using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    // #161 - SourceInstanceId is the redelivery guard's idempotency key: the OWN instance id of
    // the child PlanItem that requested this repetition (PlanItemRepetitionCriteriaMetEvent.
    // PlanItemInstanceId - see StageBehavior.HandleChildRepeated). Ported from SentryStore's
    // OccurrenceToken/IsRedelivery pattern (D5/D11) rather than inventing a new mechanism -
    // see StageBehaviorStore.IsRepetitionRedelivery for why this specific id is durable and
    // cannot mistake a legitimate new repetition for a redelivery of an old one.
    [GenerateSerializer]
    public class ChildRepeated : BaseUpdate
    {
        [Id(0)]
        public string SourceInstanceId { get; set; }
    }
}
