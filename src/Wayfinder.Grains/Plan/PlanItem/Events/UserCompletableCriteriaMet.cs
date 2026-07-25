using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan.PlanItem.Events
{
    [GenerateSerializer]
    public class UserCompletableCriteriaMet : BaseUpdate
    {
        [Id(0)]
        public bool UserCompletable { get; set; } = true;
    }
}
