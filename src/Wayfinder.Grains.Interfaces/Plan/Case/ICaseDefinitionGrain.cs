using System.Threading.Tasks;
using Orleans;

namespace Flow.Grains.Interfaces.Plan.Case
{
    public interface ICaseDefinitionGrain : IGrainWithGuidCompoundKey
    {
        Task<bool> Defined();
        Task Define(Model.Case definition);

        Task<Model.Case> GetDefinition();
    }
}
