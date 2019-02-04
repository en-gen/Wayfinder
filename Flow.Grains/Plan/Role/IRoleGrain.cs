using System.Threading.Tasks;
using Flow.Grains.Plan.CmmnElement;
using Orleans;

namespace Flow.Grains.Plan.Role
{
    public interface IRoleGrain : ICmmnElementGrain<Interfaces.Model.Role>
    {
        Task<bool> Authorize(GrainCancellationToken ct);
    }
}
