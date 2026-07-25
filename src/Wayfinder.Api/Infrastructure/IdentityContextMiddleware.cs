using System;
using System.Linq;
using System.Threading.Tasks;
using Flow.Application.Identity;
using Flow.Grains.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Flow.Api.Infrastructure
{
    // ADO #33 - the ONLY place identity enters the system. Sits after UseAuthentication/
    // UseAuthorization in Flow.Silo/Startup.cs's pipeline: by the time a request reaches here,
    // JwtBearer has already validated the token's signature/issuer/audience/expiry (AddFlowApi), and
    // the fallback authorization policy has already 401'd anything unauthenticated that wasn't
    // explicitly [AllowAnonymous]. This middleware's only job is turning an authenticated caller's
    // "sub" claim into CaseRequestContext (TenantId/UserId/UserRoles) via ITenantResolver - the
    // single seam Flow.Application.Identity exposes for exactly this (see ITenantResolver's
    // remarks). CaseRequestContext rides Orleans's RequestContext (AsyncLocal), so once set here it
    // propagates automatically to every co-hosted grain call this request makes - no further
    // plumbing needed in the Cases command/query handlers (see CaseRequestContextDefaults, deleted
    // by this same work item - this middleware is its permanent replacement).
    //
    // Deliberately skips [AllowAnonymous]-tagged endpoints entirely (health/root/cluster/OpenAPI)
    // rather than acting on context.User.IsAuthenticated alone: UseAuthentication runs
    // unconditionally for every request regardless of the matched endpoint's authorization metadata,
    // so a caller could attach a perfectly valid-but-unprovisioned bearer token to a request for
    // "/health" and would otherwise get 403'd off a route that was never supposed to require
    // identity at all. Checking the resolved endpoint's IAllowAnonymous metadata (set by UseRouting,
    // available to every later middleware) keeps this middleware inert on public routes exactly the
    // way the fallback authorization policy already is.
    public sealed class IdentityContextMiddleware
    {
        private readonly RequestDelegate _next;

        public IdentityContextMiddleware(RequestDelegate next)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
        }

        public async Task InvokeAsync(HttpContext context, ITenantResolver tenantResolver)
        {
            if (context is null) throw new ArgumentNullException(nameof(context));
            if (tenantResolver is null) throw new ArgumentNullException(nameof(tenantResolver));

            var endpoint = context.GetEndpoint();
            var isAnonymousEndpoint = endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null;

            if (isAnonymousEndpoint || context.User?.Identity?.IsAuthenticated != true)
            {
                await _next(context);
                return;
            }

            // MapInboundClaims = false (set on JwtBearerOptions in AddFlowApi) keeps the token's
            // "sub" claim literally named "sub" - no legacy ClaimTypes.NameIdentifier remap to
            // second-guess here.
            var subject = context.User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;

            var resolved = await tenantResolver.ResolveAsync(
                subject, issuer: null, cancellationToken: context.RequestAborted);

            if (!resolved.Resolved)
            {
                // Authenticated (a real, validly-signed token) but not provisioned in the tenant
                // registry - distinct from 401 (who are you?) and deliberately not 404 (this is not
                // a resource lookup; the caller's identity itself is the problem).
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            CaseRequestContext.TenantId = resolved.TenantId;
            CaseRequestContext.UserId = resolved.UserId;
            CaseRequestContext.UserRoles = resolved.Roles;

            // ADO #59 - every request authenticated here is a human user token (see the "sub"
            // claim resolution above) acting on its own authority, so the acting principal is
            // always User/null. Documented future hook: an S2S client credential path would
            // resolve to ActorPrincipalType.Client here, with ActorOnBehalfOf populated only when
            // that client asserts an end-user identity of its own (e.g. a distinct claim/header
            // this middleware doesn't examine today) - see CaseRequestContext.ActorOnBehalfOf's
            // remarks. No such path is wired up yet; this assignment just makes today's User/null
            // default explicit rather than relying on CaseRequestContext's own fallback.
            CaseRequestContext.ActorPrincipalType = ActorPrincipalType.User;
            CaseRequestContext.ActorOnBehalfOf = null;

            await _next(context);
        }
    }
}
