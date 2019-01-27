using System;
using System.Runtime.Serialization;

namespace Flow.Grains.Plan.CmmnElement
{
    public abstract class CmmnElementStore<TDefinition> : BaseState
        where TDefinition : Interfaces.Model.CmmnElement
    {
        public Guid CaseDefinitionId { get; private set; }
        public TDefinition Definition { get; private set; }

        [IgnoreDataMember]
        public bool IsDefined => Definition != null;

        public void Apply(CmmnElementDefined<TDefinition> @event)
        {
            CaseDefinitionId = @event.CaseDefinitionId ?? throw new ArgumentNullException(nameof(@event.CaseDefinitionId));
            Definition = @event.Definition;

            Created = @event.Created;
        }
    }

    [Serializable]
    public class CmmnElementDefined<TDefinition>
        where TDefinition : Interfaces.Model.CmmnElement
    {
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public Guid? CaseDefinitionId { get; set; }

        public TDefinition Definition { get; set; }
    }
}
