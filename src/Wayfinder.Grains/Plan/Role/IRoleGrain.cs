using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Plan.CmmnElementGrain;
using Orleans;

namespace Wayfinder.Grains.Plan.Role
{
    public interface IRoleGrain : ICmmnElementGrain<Interfaces.Model.Role>
    {
        Task<bool> Authorize(GrainCancellationToken ct);
    }
}
