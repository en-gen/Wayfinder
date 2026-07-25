using Wayfinder.Grains.Plan.CmmnElement;
using Orleans;

namespace Wayfinder.Grains.Plan.Role
{
    [GenerateSerializer]
    public class RoleStore : CmmnElementStore<Interfaces.Model.Role>
    {
    }
}
