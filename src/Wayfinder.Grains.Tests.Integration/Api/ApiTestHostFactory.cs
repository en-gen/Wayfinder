using System;
using System.Threading.Tasks;
using Wayfinder.Api.Controllers;
using Wayfinder.Api.DependencyInjection;
using Wayfinder.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;

namespace Wayfinder.Grains.Tests.Integration.Api
{
    // ADO #32/#33 (sub-unit 4) - builds a real ASP.NET Core TestServer wired with Wayfinder.Api's ACTUAL
    // production composition (AddWayfinderApi/MapWayfinderApi - see ServiceCollectionExtensions/
    // EndpointRouteBuilderExtensions in Wayfinder.Api), but backed by the SAME in-memory Orleans
    // TestCluster every other suite in this project shares (ClusterFixture) instead of a second
    // real co-hosted silo (Wayfinder.Silo/Program.cs's UseOrleans, which would mean either a real
    // localhost Orleans runtime or real Azure Table clustering just to run this test). This proves
    // the REAL request pipeline end-to-end over HTTP - auth -> the fallback authorization policy ->
    // IdentityContextMiddleware -> ITenantResolver -> CaseRequestContext -> ISender -> the command/
    // query handlers -> grain-level enforcement (CrossTenantAccessException) -> ResultExtensions'
    // 404 mapping - exactly the way a real deployment's request flows.
    //
    // Only the authentication SCHEME differs from production: TestAuthenticationHandler stamps a
    // configurable "sub" claim from a request header instead of validating a real Zitadel-signed
    // bearer token (see that type's remarks for how it coexists with, rather than replaces,
    // AddWayfinderApi's own JwtBearer registration). Everything else - controllers, OData, versioning,
    // the fallback authorization policy, the identity middleware, AddWayfinderApplication/AddWayfinderIdentity,
    // ResultExtensions - is the untouched production composition.
    internal static class ApiTestHostFactory
    {
        public static async Task<IHost> StartAsync(IClusterClient clusterClient)
        {
            if (clusterClient is null) throw new ArgumentNullException(nameof(clusterClient));

            var hostBuilder = new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder
                        .UseTestServer()
                        .ConfigureServices(services =>
                        {
                            // Stands in for what a co-hosted Orleans silo would register
                            // automatically in production (Wayfinder.Silo/Startup.cs's remarks) - the
                            // handlers (Wayfinder.Application.Cases.*) only ever ask for IClusterClient.
                            services.AddSingleton(clusterClient);

                            services.AddWayfinderApi();

                            // AddWayfinderApi's AddControllers() call discovers controllers via the
                            // DEFAULT ApplicationPartManager, which seeds itself from
                            // Assembly.GetEntryAssembly() - under a unit test runner that is the
                            // test host process, not this project, so Wayfinder.Api's controllers would
                            // otherwise never be found (a well-known ASP.NET Core testing pitfall,
                            // not a getFlowApi bug). Explicitly registering Wayfinder.Api's assembly as
                            // an ApplicationPart is the standard fix - additive to (not a
                            // replacement of) whatever AddWayfinderApi's own AddControllers() already
                            // registered, since ApplicationPartManager is a singleton every
                            // AddControllers() call shares.
                            services
                                .AddControllers()
                                .ConfigureApplicationPartManager(parts =>
                                    parts.ApplicationParts.Add(new AssemblyPart(typeof(CasesController).Assembly)));

                            // Overrides AddWayfinderApi's JwtBearer-only authentication for this host
                            // only - see TestAuthenticationHandler's remarks for why this is
                            // additive (a second "TestScheme" registration + a DefaultScheme
                            // repoint) rather than a replacement of the "Bearer" scheme AddWayfinderApi
                            // already registered.
                            services
                                .AddAuthentication(TestAuthenticationHandler.SchemeName)
                                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                                    TestAuthenticationHandler.SchemeName, _ => { });
                        })
                        .Configure(app =>
                        {
                            // Mirrors Wayfinder.Silo/Startup.cs's Configure exactly (see that file's
                            // remarks on ordering) minus the /health, /, /cluster diagnostics
                            // endpoints, which are Wayfinder.Silo-specific and irrelevant here.
                            app.UseRouting();
                            app.UseAuthentication();
                            app.UseAuthorization();
                            app.UseMiddleware<IdentityContextMiddleware>();
                            app.UseEndpoints(endpoints => endpoints.MapWayfinderApi());
                        });
                });

            return await hostBuilder.StartAsync();
        }
    }
}
