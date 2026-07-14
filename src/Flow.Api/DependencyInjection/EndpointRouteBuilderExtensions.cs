using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Flow.Api.DependencyInjection
{
    // ADO #32/#33 - maps everything AddFlowApi registered. Called from Flow.Silo/Startup.cs's
    // UseEndpoints, alongside the existing /health, /, /cluster MapGet calls (which stay mapped
    // there directly, [AllowAnonymous] - see that file).
    public static class EndpointRouteBuilderExtensions
    {
        public static IEndpointRouteBuilder MapFlowApi(this IEndpointRouteBuilder endpoints)
        {
            if (endpoints is null) throw new ArgumentNullException(nameof(endpoints));

            endpoints.MapControllers();

            // The OpenAPI document itself is public - describing the API's shape is not a
            // protected operation, and a caller needs it before it can even acquire a token for the
            // right audience.
            endpoints.MapOpenApi().AllowAnonymous();

            return endpoints;
        }
    }
}
