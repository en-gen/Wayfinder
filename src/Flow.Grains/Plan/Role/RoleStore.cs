using Flow.Grains.Plan.CmmnElement;
using Orleans;

namespace Flow.Grains.Plan.Role
{
    [GenerateSerializer]
    public class RoleStore : CmmnElementStore<Interfaces.Model.Role>
    {
    }
}
