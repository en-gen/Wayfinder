using System.Threading.Tasks;
using Flow.Grains.Interfaces.Plan.CmmnElementGrain;
using Orleans;

namespace Flow.Grains.Plan.Role
{
    public interface IRoleGrain : ICmmnElementGrain<Interfaces.Model.Role>
    {
        Task<bool> Authorize(GrainCancellationToken ct);
    }
}
