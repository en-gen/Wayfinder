using System;
using System.Collections.Generic;
using Wayfinder.Grains.Interfaces.Identity;

namespace Wayfinder.Application.Identity
{
    // ADO #33 - one user's seed entry within a TenantSeed: the subject (the OIDC/Zitadel "sub"
    // claim) that IUserIdentityGrain is keyed by, plus the internal UserId/Roles it should resolve
    // to.
    public sealed class UserSeed
    {
        public string Subject { get; init; }

        public Guid UserId { get; init; }

        public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
    }

    // ADO #33 - explicit seed data for one tenant: its own metadata/OIDC config plus every user
    // that should resolve into it. IdentityRegistrySeeder.SeedAsync takes one of these at a time
    // so First Light (a later sub-unit) and the isolation tests (sub-unit 4) can build at least
    // two independent TenantSeed instances and prove the wall between them.
    public sealed class TenantSeed
    {
        public Guid TenantId { get; init; }

        public string Name { get; init; }

        public TenantOidcConfig Oidc { get; init; }

        public IReadOnlyList<UserSeed> Users { get; init; } = Array.Empty<UserSeed>();
    }
}
