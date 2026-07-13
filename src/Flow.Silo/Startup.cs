using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;

namespace Flow.Silo
{
    // Minimal, dependency-light diagnostics ONLY (work item #49) - explicitly NOT the
    // case-operation REST API (work item #32, out of scope here). Two endpoints:
    //
    //   GET /health          - bare liveness probe: 200 once Kestrel is serving requests at
    //                          all. Backs the container HEALTHCHECK (src/Flow.Silo/Dockerfile).
    //   GET / and GET /cluster - cluster-status: how many silos the co-hosted Orleans CLIENT
    //                          currently sees as Active, via IManagementGrain.GetHosts. This is
    //                          the crux observability hook for #49's eval stack - curling this
    //                          on any one of the N silo containers proves whether the cluster
    //                          actually formed over real Azure Table membership (Azurite in the
    //                          eval stack), not merely that each container started. Both routes
    //                          serve the identical payload; "/" keeps the endpoint reachable at
    //                          the silo's root the way the previous "Hello World!" placeholder
    //                          was, "/cluster" is the more explicit name for the same query.
    //
    // The co-hosted process is BOTH an Orleans silo (UseOrleans in Program.cs) and an Orleans
    // client - Orleans registers a local IClusterClient/IGrainFactory into the SAME DI
    // container automatically for a co-hosted silo, so it resolves here via
    // context.RequestServices with no separate client bootstrap.
    public class Startup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/health", async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    await context.Response.WriteAsync("Healthy");
                });

                endpoints.MapGet("/", context => WriteClusterStatusAsync(context, logger));
                endpoints.MapGet("/cluster", context => WriteClusterStatusAsync(context, logger));
            });
        }

        // Deliberately tolerant of "cluster not formed yet" (silo just started and hasn't
        // joined membership, or the co-hosted client hasn't connected to a gateway yet) - this
        // endpoint is meant to be curled DURING cluster bring-up, not only once steady-state is
        // reached. Always returns 200; a caller distinguishes "formed" from "not yet" via the
        // "activeSilos" count / presence of "error" in the body, not via HTTP status, so a
        // container orchestrator's readiness probe (if ever pointed here) does not flap while
        // the cluster is still converging.
        private static async Task WriteClusterStatusAsync(HttpContext context, ILogger logger)
        {
            context.Response.ContentType = "application/json";

            try
            {
                var clusterClient = context.RequestServices.GetRequiredService<IClusterClient>();
                var managementGrain = clusterClient.GetGrain<IManagementGrain>(0);
                var hosts = await managementGrain.GetHosts(onlyActive: true);

                var payload = new
                {
                    activeSilos = hosts.Count,
                    silos = hosts
                        .Select(kvp => new { address = kvp.Key.ToString(), status = kvp.Value.ToString() })
                        .ToArray()
                };

                await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Cluster status query failed - the co-hosted Orleans client may not be " +
                    "connected to a gateway yet");

                var payload = new
                {
                    activeSilos = 0,
                    silos = Array.Empty<object>(),
                    error = ex.Message
                };

                await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
            }
        }
    }
}
