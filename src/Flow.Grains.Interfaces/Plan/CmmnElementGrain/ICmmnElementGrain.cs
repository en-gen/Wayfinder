using System.Threading.Tasks;
using Orleans;

namespace Flow.Grains.Interfaces.Plan.CmmnElementGrain
{
    public interface ICmmnElementGrain<in TDefinition> : IGrainWithGuidCompoundKey
        where TDefinition : Model.CmmnElement
    {
        Task<bool> Defined();
        Task Define(string caseDefinitionId, TDefinition definition);
    }
}
