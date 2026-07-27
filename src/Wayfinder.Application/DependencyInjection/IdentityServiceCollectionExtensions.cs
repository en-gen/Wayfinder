using System;
using Wayfinder.Application.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Wayfinder.Application.DependencyInjection
{
    // ADO #33 - the identity/tenant-registry DI seam, a sibling AddWayfinderApplication calls (see
    // ServiceCollectionExtensions.AddWayfinderApplication) rather than folding into that method's
    // handler-assembly-scan body. Kept as its own extension so a host that only needs the resolver
    // (unlikely today, but keeps the seam honest) could call it standalone.
    public static class IdentityServiceCollectionExtensions
    {
        public static IServiceCollection AddWayfinderIdentity(this IServiceCollection services)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));

            // Scoped to match ISender/the command/query handlers (AddWayfinderApplication) - one
            // instance per resolved scope, consistent lifetime story across the Application layer.
            services.AddScoped<ITenantResolver, TenantResolver>();
            services.AddScoped<IIdentityRegistrySeeder, IdentityRegistrySeeder>();

            return services;
        }
    }
}
