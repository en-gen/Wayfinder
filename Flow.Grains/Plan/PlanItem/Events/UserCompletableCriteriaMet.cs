using System;
using Flow.Grains.Plan.CmmnElement.Events;

namespace Flow.Grains.Plan.PlanItem.Events
{
    [Serializable]
    public class UserCompletableCriteriaMet : BaseUpdate
    {
        public bool UserCompletable { get; set; } = true;
    }
}