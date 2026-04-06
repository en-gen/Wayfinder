using System.Linq;
using System.Threading.Tasks;
using Flow.Grains.Interfaces;
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
                () => State.Defined && CaseRequestContext.UserRoles.Contains(Definition.Name),
                ct.CancellationToken);
    }
}
