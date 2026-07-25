using System;
using Orleans;

namespace Wayfinder.Grains.Interfaces.Identity
{
    // ADO #33 - what ITenantGrain hands back for a seeded tenant. TenantId is redundant with the
    // grain's own primary key (this.GetPrimaryKey()) but carried on the record anyway so callers
    // that only have the record (not the grain reference) don't need a second round trip.
    [GenerateSerializer]
    public class TenantRecord
    {
        [Id(0)]
        public Guid TenantId { get; set; }

        [Id(1)]
        public string Name { get; set; }

        [Id(2)]
        public TenantOidcConfig Oidc { get; set; }
    }
}
