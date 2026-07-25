using System;
using Orleans;

namespace Flow.Grains.Interfaces.Identity
{
    // ADO #33 - the tenant-registry projection of a resolved identity: what IUserIdentityGrain
    // hands back to a caller (Flow.Application's ITenantResolver today; a future HTTP auth
    // middleware, sub-unit 3). Deliberately NOT the same type as Flow.Application's
    // ResolvedIdentity - this is the grain-layer DTO, kept free of any app-layer concerns so
    // Flow.Grains.Interfaces stays the thin contract Flow.Application projects from (mirrors
    // CaseSnapshot -> CaseView via CaseViewProjector).
    [GenerateSerializer]
    public class UserIdentityRecord
    {
        [Id(0)]
        public Guid TenantId { get; set; }

        [Id(1)]
        public Guid UserId { get; set; }

        [Id(2)]
        public string[] Roles { get; set; } = Array.Empty<string>();
    }
}
