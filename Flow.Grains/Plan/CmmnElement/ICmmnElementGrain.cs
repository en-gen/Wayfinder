using System;
using System.Threading.Tasks;
using Orleans;

namespace Flow.Grains.Plan.CmmnElement
{
    public interface ICmmnElementGrain<in TDefinition> : IGrainWithGuidCompoundKey
        where TDefinition : Interfaces.Model.CmmnElement
    {
        Task<bool> Defined();
        Task Define(Guid caseDefinitionId, TDefinition definition);
    }
}
