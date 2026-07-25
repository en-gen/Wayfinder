using System.Threading.Tasks;
using Orleans;

namespace Flow.Grains.Interfaces.Identity
{
    // ADO #33 - per-tenant metadata + OIDC config, keyed by TenantId (Guid) itself
    // (IGrainWithGuidKey - a bare tenant id, no compound key, unlike CmmnElementGrain's
    // (TenantId, address) compound keys: a tenant grain's whole existence IS its tenant id).
    // Reference/config data, not an evented aggregate - same reasoning as IUserIdentityGrain.
    public interface ITenantGrain : IGrainWithGuidKey
    {
        // Seeds/overwrites this tenant's metadata. Idempotent, same as
        // IUserIdentityGrain.Register - re-seeding replaces the prior record.
        Task Seed(string name, TenantOidcConfig oidc);

        // Never throws for a never-seeded tenant id - returns null (an Orleans grain always
        // "exists" as a virtual actor; "never seeded" is the not-found signal, same pattern as
        // CaseGrain.GetSnapshot's null-Definition for a never-created case).
        Task<TenantRecord> Get();
    }
}
