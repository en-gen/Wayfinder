using System.Collections.Generic;
using Orleans;

namespace Flow.Grains.Interfaces.Identity
{
    // ADO #33 - a tenant's own OIDC/SSO federation config, held so a future per-tenant SSO
    // federation feature (#74) is purely additive: ITenantGrain already has a place to hold
    // "which issuer/audience/JWKS this tenant's tokens come from" without a further grain-shape
    // change. Unused by sub-unit 3's resolver (which only needs the subject -> identity mapping),
    // but seedable/gettable now so #74 only has to add validation logic against an existing field,
    // not a new grain method. ClaimMappings is a plain Dictionary<string,string> (both Orleans's
    // native codegen and the reflection-JSON fallback round-trip it fine) rather than
    // System.Text.Json.Nodes - see Flow.Silo/Program.cs's GrainStorageSerializer remarks for why
    // JsonNode values are the thing to avoid in [PersistentState] state.
    [GenerateSerializer]
    public class TenantOidcConfig
    {
        [Id(0)]
        public string Issuer { get; set; }

        [Id(1)]
        public string Audience { get; set; }

        // Discovery/JWKS endpoint (e.g. Zitadel's ".well-known/openid-configuration" URL) a future
        // token-validation middleware fetches signing keys from.
        [Id(2)]
        public string MetadataAddress { get; set; }

        // Optional claim-name overrides (e.g. a tenant whose IdP emits "roles" under a
        // non-standard claim). Empty/null means "use the engine's own defaults".
        [Id(3)]
        public Dictionary<string, string> ClaimMappings { get; set; } = new();
    }
}
