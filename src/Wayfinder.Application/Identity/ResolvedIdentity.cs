using System;
using System.Collections.Generic;

namespace Wayfinder.Application.Identity
{
    // ADO #33 - the app-layer result of resolving a token subject against the tenant registry.
    // Deliberately a distinct type from Wayfinder.Grains.Interfaces.Identity.UserIdentityRecord (the
    // grain-layer DTO): Wayfinder.Application projects the grain contract into its own shape, same
    // layering CaseViewProjector uses for CaseSnapshot -> CaseView. This is the single seam a
    // future HTTP auth middleware (sub-unit 3) calls to populate CaseRequestContext
    // (TenantId/UserId/UserRoles) from an authenticated caller's subject.
    public sealed class ResolvedIdentity
    {
        private static readonly string[] NoRoles = Array.Empty<string>();

        private ResolvedIdentity(bool resolved, Guid tenantId, Guid userId, IReadOnlyList<string> roles)
        {
            Resolved = resolved;
            TenantId = tenantId;
            UserId = userId;
            Roles = roles ?? NoRoles;
        }

        public bool Resolved { get; }

        public Guid TenantId { get; }

        public Guid UserId { get; }

        public IReadOnlyList<string> Roles { get; }

        // A single shared instance is safe - this type is immutable and carries no per-call state.
        public static ResolvedIdentity NotResolved { get; } =
            new ResolvedIdentity(false, Guid.Empty, Guid.Empty, NoRoles);

        public static ResolvedIdentity Found(Guid tenantId, Guid userId, IReadOnlyList<string> roles) =>
            new ResolvedIdentity(true, tenantId, userId, roles);
    }
}
