using System;
using Flow.Grains.Plan.CmmnElement.Events;

namespace Flow.Grains.Plan
{
    public class BaseStore
    {
        public DateTime? Created { get; protected set; }
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
