using System;
using System.Collections.Generic;

namespace Wayfinder.Silo.Infrastructure.Options
{
    // ADO #32/#33 (sub-unit 4, P1/First Light) - explicit seed data for the Docker/eval
    // environment ONLY (see Program.cs's SeedEvalIdentityRegistryAsync, gated to
    // HostingEnvironment.EnvironmentName == "Docker" - never Development, never Production).
    // Maps each real Zitadel subject (the token's "sub" claim, minted by the compose Zitadel once
    // its bootstrap org/human users are created - see devops/eval/README.md's manual bootstrap
    // steps) to a Case.Flow tenant/user/role set via Wayfinder.Application.Identity.IIdentityRegistrySeeder,
    // the exact seam sub-unit 4's isolation tests also use. Bound from the "EvalIdentity" config
    // section - devops/eval/docker-compose.yml supplies it via EvalIdentity__Tenants__<n>__* env
    // vars (indexed-array binding is native to Microsoft.Extensions.Configuration, no custom code
    // needed). A tenant entry with a blank Subject is skipped (see Program.cs) - the compose file
    // ships with placeholder/blank subjects until a developer completes the manual Zitadel
    // bootstrap and fills in the real ones.
    public sealed class EvalIdentityOptions
    {
        public const string ConfigKey = "EvalIdentity";

        public List<EvalTenantSeed> Tenants { get; set; } = new();
    }

    public sealed class EvalTenantSeed
    {
        public Guid TenantId { get; set; }

        public string Name { get; set; }

        // The Zitadel-issued "sub" claim for this tenant's demo user - IdentityContextMiddleware
        // reads this same claim in production; here it is the join key the seeder uses to register
        // Wayfinder.Application.Identity.UserSeed against Wayfinder.Grains.Interfaces.Identity.IUserIdentityGrain.
        public string Subject { get; set; }

        public Guid UserId { get; set; }

        public string Issuer { get; set; }

        public string Audience { get; set; }

        public string MetadataAddress { get; set; }

        public string[] Roles { get; set; } = Array.Empty<string>();
    }
}
