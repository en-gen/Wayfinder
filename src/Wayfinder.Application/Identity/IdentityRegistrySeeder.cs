using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Identity;
using Orleans;

namespace Wayfinder.Application.Identity
{
    public sealed class IdentityRegistrySeeder : IIdentityRegistrySeeder
    {
        private readonly IClusterClient _clusterClient;

        public IdentityRegistrySeeder(IClusterClient clusterClient)
        {
            _clusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
        }

        public async Task SeedAsync(TenantSeed tenant, CancellationToken cancellationToken = default)
        {
            if (tenant is null) throw new ArgumentNullException(nameof(tenant));

            var tenantGrain = _clusterClient.GetGrain<ITenantGrain>(tenant.TenantId);
            await tenantGrain.Seed(tenant.Name, tenant.Oidc);

            foreach (var user in tenant.Users ?? Array.Empty<UserSeed>())
            {
                var userIdentityGrain = _clusterClient.GetGrain<IUserIdentityGrain>(user.Subject);
                await userIdentityGrain.Register(new UserIdentityRecord
                {
                    TenantId = tenant.TenantId,
                    UserId = user.UserId,
                    Roles = (user.Roles ?? Array.Empty<string>()).ToArray()
                });
            }
        }
    }
}
