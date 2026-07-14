using System;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Flow.Grains.Tests.Integration.Api
{
    // ADO #32/#33 (sub-unit 4) - stands in for JwtBearer in the full-pipeline isolation suite
    // (see ApiTestHostFactory): stamps a caller-supplied "sub" claim from a plain request header
    // instead of validating a real Zitadel-signed bearer token, so a test can act as tenant A's
    // user vs tenant B's user without minting real JWTs. Registered as its OWN scheme
    // (ApiTestHostFactory repoints AuthenticationOptions.DefaultScheme at it) rather than replacing
    // AddFlowApi's "Bearer"/JwtBearer registration in place - both schemes end up registered, but
    // only this one is ever selected, so AddFlowApi's production authentication wiring is never
    // touched by this test-only type. Downstream of authentication, everything is the real
    // production pipeline: the fallback authorization policy (RequireAuthenticatedUser) does not
    // care which scheme authenticated the caller, and IdentityContextMiddleware reads the "sub"
    // claim exactly the same way regardless of which handler produced it.
    public sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestScheme";

        // A caller sets this header to the subject they want to authenticate as; omitting it
        // entirely is how a test proves the unauthenticated (401) path.
        public const string SubjectHeaderName = "X-Test-Subject";

        public TestAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(SubjectHeaderName, out StringValues subjectValues) ||
                StringValues.IsNullOrEmpty(subjectValues))
            {
                // NoResult (not Fail) - mirrors JwtBearer's own behavior for a request that carries
                // no bearer token at all, so the fallback authorization policy's challenge produces
                // a plain 401 rather than something else.
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            // Deliberately literal "sub" (no ClaimTypes remap) - matches AddFlowApi's
            // JwtBearerOptions.MapInboundClaims = false, which IdentityContextMiddleware relies on
            // to read the claim back out under the same literal name.
            var claims = new[] { new Claim("sub", subjectValues.ToString()) };
            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
