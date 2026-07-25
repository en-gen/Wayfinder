using System;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Wayfinder.Grains.Plan
{
    [GenerateSerializer]
    public class BaseStore
    {
        [Id(0)]
        public DateTime? Created { get; protected set; }
        [Id(1)]
        public DateTime? Updated { get; protected set; }

        public void Apply(BaseCreated @event)
        {
            Created = @event.Created;
        }

        public void Apply(BaseUpdate @event)
        {
            Updated = @event.Updated;
        }
    }
}
