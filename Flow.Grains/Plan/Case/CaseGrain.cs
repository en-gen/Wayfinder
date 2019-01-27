using System.Threading.Tasks;
using Flow.Grains.Interfaces.Plan.Case;
using Orleans;

namespace Flow.Grains.Plan.Case
{
    public class CaseGrain :
        Grain,
        ICaseGrain
    {
        public Task<string> HelloWorld()
        {
            return Task.FromResult("Hello World!");
        }
    }
}
