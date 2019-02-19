using System;
using System.Collections.Generic;
using Flow.Grains.Plan.PlanItem.Events;

namespace Flow.Grains.Plan.PlanItem.Behaviors.Stores
{
    [Serializable]
    public class StageBehaviorStore
    {
        // only applicable to PlanItems defined by a Stage
        // PlanItemDefinitionId => PlanItemInstanceId => Repetition
        public IDictionary<string, IDictionary<string, int>> Children { get; } = new Dictionary<string, IDictionary<string, int>>();
        
        public void Apply(ChildCreated @event)
        {
            if (!Children.TryGetValue(@event.PlanItemDefinitionId, out var instances))
            {
                instances = new Dictionary<string, int>();
                Children[@event.PlanItemDefinitionId] = instances;
            }

            instances[@event.PlanItemInstanceId] = @event.Repetition;
        }
    }
}