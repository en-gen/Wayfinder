using System;
using System.Runtime.Serialization;
using Flow.Grains.Plan.CmmnElement.Events;

namespace Flow.Grains.Plan.CmmnElement
{
    public abstract class CmmnElementStore<TDefinition> : BaseStore
        where TDefinition : Interfaces.Model.CmmnElement
    {
        public string CaseDefinitionId { get; private set; }
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
