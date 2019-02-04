using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Plan.Role;
using Flow.Grains.Plan.CmmnElement;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Flow.Grains.Plan.Role
{
    public class RoleGrain : CmmnElementGrain<RoleStore, Interfaces.Model.Role>, IRoleGrain
    {
        public RoleGrain(ILogger<RoleGrain> logger) :
            base(logger)
        {
        }

        public Task<bool> Authorize(GrainCancellationToken ct) =>
            Task.Factory.StartNew(
                () => State.Defined && RoleRegistrar.GetRoles().Contains(Definition.Name),
                ct.CancellationToken);
    }
}
