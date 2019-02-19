using System;

namespace Flow.Grains.Plan
{
    public class BaseStore
    {
        public DateTime? Created { get; protected set; }
        public DateTime? Updated { get; protected set; }
    }
}
