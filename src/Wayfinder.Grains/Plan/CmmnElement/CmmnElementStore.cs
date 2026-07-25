using System;
using System.Runtime.Serialization;
using Flow.Grains.Plan.CmmnElement.Events;
using Orleans;

namespace Flow.Grains.Plan.CmmnElement
{
    [GenerateSerializer]
    public abstract class CmmnElementStore<TDefinition> : BaseStore
        where TDefinition : Interfaces.Model.CmmnElement
    {
        [Id(0)]
        public string CaseDefinitionId { get; private set; }
        [Id(1)]
        public TDefinition Definition { get; private set; }

        [IgnoreDataMember]
        public bool Defined => Definition != null;

        public void Apply(CmmnElementDefined<TDefinition> @event)
        {
            base.Apply(@event);
            CaseDefinitionId = @event.CaseDefinitionId ?? throw new ArgumentNullException(nameof(@event.CaseDefinitionId));
            Definition = @event.Definition;
        }
    }
}
