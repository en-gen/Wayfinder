using System.Threading;
using System.Threading.Tasks;

namespace Wayfinder.Application.Identity
{
    // ADO #33 - the explicit seeding seam for the tenant registry: First Light (a later sub-unit)
    // and integration tests (this sub-unit, sub-unit 4) call this to populate ITenantGrain/
    // IUserIdentityGrain instead of reaching for the grains directly. Deliberately NOT wired into
    // Wayfinder.Silo/Program.cs yet - sub-unit 4 owns when/how First Light actually seeds its demo
    // tenants.
    public interface IIdentityRegistrySeeder
    {
        Task SeedAsync(TenantSeed tenant, CancellationToken cancellationToken = default);
    }
}
