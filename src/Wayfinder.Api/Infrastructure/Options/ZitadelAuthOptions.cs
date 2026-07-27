namespace Wayfinder.Api.Infrastructure.Options
{
    // ADO #33 - the single trusted token issuer for this deployment: our Zitadel instance.
    // Deliberately NOT hardcoded anywhere - Authority/Audience are bound from configuration
    // ("Auth:Zitadel" - see ServiceCollectionExtensions.AddWayfinderApi) so every environment (local dev
    // against a docker Zitadel, First Light, production) supplies its own values via
    // appsettings/environment variables (AUTH__ZITADEL__AUTHORITY / AUTH__ZITADEL__AUDIENCE - the
    // "__" separator maps to ":" section nesting, same convention as ORLEANS__ADVERTISEDIPADDRESS
    // in Wayfinder.Silo/Program.cs). Distinct from Wayfinder.Grains.Interfaces.Identity.TenantOidcConfig,
    // which is a PER-TENANT federation config for a future feature (#74); this is the ONE issuer
    // JwtBearer itself trusts to sign a token in the first place, before any tenant lookup happens.
    public sealed class ZitadelAuthOptions
    {
        public const string ConfigKey = "Auth:Zitadel";

        public string Authority { get; set; }

        public string Audience { get; set; }
    }
}
