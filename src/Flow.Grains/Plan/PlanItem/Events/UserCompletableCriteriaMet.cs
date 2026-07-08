using System;
using Flow.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class UserCompletableCriteriaMet : BaseUpdate
    {
        [Id(0)]
        public bool UserCompletable { get; set; } = true;
    }
}