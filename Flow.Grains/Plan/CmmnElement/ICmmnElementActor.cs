using System;
using System.Threading.Tasks;
using Orleans;

namespace Flow.Grains.Plan.CmmnElement
{
    public interface ICmmnElementActor<in TDefinition> : IGrainWithGuidCompoundKey
        where TDefinition : Interfaces.Model.CmmnElement
    {
        Task Define(Guid caseDefinitionId, TDefinition definition);
    }
}
