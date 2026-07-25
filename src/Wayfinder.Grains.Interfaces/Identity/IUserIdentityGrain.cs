using System.Threading.Tasks;
using Orleans;

namespace Wayfinder.Grains.Interfaces.Identity
{
    // ADO #33 - the tenant registry's subject -> identity lookup. Keyed by the bare OIDC/Zitadel
    // "sub" claim string (IGrainWithStringKey), so the key IS the subject - no compound key, no
    // tenant scoping on the key itself, because resolving the tenant FROM the subject is exactly
    // this grain's job (a caller cannot know the tenant yet - that's the whole point of the
    // registry). Reference data, not an evented aggregate: a plain Grain backed by
    // [PersistentState(_, "Default")], NOT a JournaledGrain like CmmnElementGrain's subclasses -
    // see UserIdentityGrain's remarks.
    public interface IUserIdentityGrain : IGrainWithStringKey
    {
        // Seeds/overwrites this subject's resolved identity. Idempotent - re-registering the same
        // subject simply replaces the prior record (used by IdentityRegistrySeeder and directly by
        // tests); never throws for an unknown vs. known subject, since "unknown" isn't a distinct
        // state prior to Register.
        Task Register(UserIdentityRecord identity);

        // Never throws for an unregistered subject - returns null. A thrown exception here would
        // let an unauthenticated/unknown caller distinguish "wrong token" from "server error";
        // the resolver (ITenantResolver) turns null into a not-resolved result, and a future HTTP
        // middleware maps not-resolved to 401, same as CrossTenantAccessException's guidance for
        // 404 (see that type's remarks) - never leak more than necessary to a caller who does not
        // already have a matching identity.
        Task<UserIdentityRecord> TryResolve();
    }
}
