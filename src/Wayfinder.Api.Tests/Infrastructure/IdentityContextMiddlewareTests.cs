using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Wayfinder.Api.Infrastructure;
using Wayfinder.Application.Identity;
using Wayfinder.Grains.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Wayfinder.Api.Tests.Infrastructure
{
    // ADO #33 - the identity middleware is the ONLY place identity enters the system (see that
    // type's remarks); these tests exercise it directly against a bare DefaultHttpContext and a
    // faked ITenantResolver, no host/TestServer.
    public class IdentityContextMiddlewareTests
    {
        [Fact]
        public async Task InvokeAsync_Given_ResolvedIdentity_Then_SetsCaseRequestContextAndCallsNext()
        {
            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var roles = new[] { "case-admin" };

            var resolver = new Mock<ITenantResolver>();
            resolver
                .Setup(r => r.ResolveAsync("sub-123", null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ResolvedIdentity.Found(tenantId, userId, roles));

            var nextCalled = false;
            Guid observedTenantId = default;
            Guid observedUserId = default;
            string[] observedRoles = null;

            // Asserted from WITHIN the downstream delegate deliberately, not from this test method
            // after `await middleware.InvokeAsync(...)` returns: CaseRequestContext rides Orleans's
            // AsyncLocal-backed RequestContext, and a mutation made inside an awaited callee (after
            // any await point) is only visible to code that callee goes on to call - it does not
            // flow back "up" to the original caller once the callee's Task has completed (standard
            // ExecutionContext semantics; verified with a minimal repro during development). "next"
            // is exactly the vantage point a real downstream controller/grain call has in
            // production, so it is the only place this guarantee can honestly be observed from.
            var middleware = new IdentityContextMiddleware(_ =>
            {
                nextCalled = true;
                observedTenantId = CaseRequestContext.TenantId;
                observedUserId = CaseRequestContext.UserId;
                observedRoles = CaseRequestContext.UserRoles.ToArray();
                return Task.CompletedTask;
            });

            var context = AuthenticatedContext("sub-123");

            await middleware.InvokeAsync(context, resolver.Object);

            nextCalled.Should().BeTrue();
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK, "no status was set - the pipeline continued");
            observedTenantId.Should().Be(tenantId);
            observedUserId.Should().Be(userId);
            observedRoles.Should().BeEquivalentTo(roles);
        }

        [Fact]
        public async Task InvokeAsync_Given_UnresolvedIdentity_Then_ShortCircuits403AndSkipsNext()
        {
            var resolver = new Mock<ITenantResolver>();
            resolver
                .Setup(r => r.ResolveAsync("sub-456", null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ResolvedIdentity.NotResolved);

            var nextCalled = false;
            var middleware = new IdentityContextMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            var context = AuthenticatedContext("sub-456");

            await middleware.InvokeAsync(context, resolver.Object);

            nextCalled.Should().BeFalse("an authenticated-but-unprovisioned caller must not reach the pipeline");
            context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        }

        [Fact]
        public async Task InvokeAsync_Given_UnauthenticatedCaller_Then_SkipsResolutionAndCallsNext()
        {
            var resolver = new Mock<ITenantResolver>();

            var nextCalled = false;
            var middleware = new IdentityContextMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity()), // no authenticationType => IsAuthenticated == false
            };

            await middleware.InvokeAsync(context, resolver.Object);

            nextCalled.Should().BeTrue();
            resolver.Verify(
                r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "401 is the auth middleware's job, not this one's - an unauthenticated caller never reaches tenant resolution");
        }

        [Fact]
        public async Task InvokeAsync_Given_AllowAnonymousEndpoint_Then_SkipsResolutionEvenIfAuthenticated()
        {
            var resolver = new Mock<ITenantResolver>();

            var nextCalled = false;
            var middleware = new IdentityContextMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

            var context = AuthenticatedContext("sub-789");
            context.SetEndpoint(new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(new AllowAnonymousAttribute()),
                "health"));

            await middleware.InvokeAsync(context, resolver.Object);

            nextCalled.Should().BeTrue();
            resolver.Verify(
                r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "a stray bearer token on a public route (health/cluster/OpenAPI) must not turn into a 403");
        }

        private static DefaultHttpContext AuthenticatedContext(string subject) => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("sub", subject) }, authenticationType: "TestScheme")),
        };
    }
}
