using System.Threading.Tasks;
using Orleans;

namespace Flow.Grains.Interfaces.Plan.Case
{
    public interface ICaseGrain : IGrainWithGuidKey
    {
        Task<string> HelloWorld();
    }
}
