using System;
using Asp.Versioning;
using Flow.Api.Infrastructure;
using Flow.Api.Infrastructure.Options;
using Flow.Application.DependencyInjection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Flow.Api.DependencyInjection
{
    // ADO #32/#33 - the Flow.Api plug-in seam, same Add{X}/Use{X} convention Flow.Application's
    // AddFlowApplication established: explicit composition, no reflection/module system beyond the
    // handler-assembly scan AddFlowApplication already does on its own. Flow.Silo/Program.cs calls
    // AddFlowApi() once from ConfigureServices; Startup.cs's Configure calls MapFlowApi (see
    // EndpointRouteBuilderExtensions) from inside UseEndpoints.
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddFlowApi(this IServiceCollection services)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));

            // The CQRS core (ISender + every command/query handler) plus the identity/tenant-
            // registry seam (ITenantResolver) IdentityContextMiddleware depends on - see
            // Flow.Application.DependencyInjection.IdentityServiceCollectionExtensions.AddFlowIdentity,
            // called transitively from here.
            services.AddFlowApplication();

            services
                .AddControllers()
                .AddOData(odata => odata
                    .AddRouteComponents("api/v1", CasesEdmModel.Build())
                    .Select()
                    .Expand());

            // ADO #32 - URL-segment versioning (api/v1/..., additive toward a future v2). See
            // Flow.Api.csproj's remarks for why this is plain Asp.Versioning.Mvc/.ApiExplorer rather
            // than Asp.Versioning.OData (no stable net10 release of the latter yet).
            services
                .AddApiVersioning(options =>
                {
                    options.DefaultApiVersion = new ApiVersion(1, 0);
                    options.AssumeDefaultVersionWhenUnspecified = false;
                    options.ReportApiVersions = true;
                    options.ApiVersionReader = new UrlSegmentApiVersionReader();
                })
                .AddMvc()
                .AddApiExplorer(options =>
                {
                    options.GroupNameFormat = "'v'VVV";
                    options.SubstituteApiVersionInUrl = true;
                });

            // net10's built-in Microsoft-owned OpenAPI document generator (Microsoft.AspNetCore.
            // OpenApi) - the doc endpoint itself is mapped [AllowAnonymous] in
            // EndpointRouteBuilderExtensions.MapFlowApi.
            services.AddOpenApi();

            AddAuthentication(services);

            // Fallback policy: every endpoint requires an authenticated caller unless it opts out
            // with [AllowAnonymous] - only /health, /, /cluster, and the OpenAPI doc endpoint do
            // (see Flow.Silo/Startup.cs).
            services.AddAuthorization(options =>
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();
            });

            return services;
        }

        // ADO #33 - single trusted issuer (our Zitadel) via JwtBearer. Authority/Audience are never
        // hardcoded - both are bound from the "Auth:Zitadel" configuration section (env-overridable
        // via AUTH__ZITADEL__AUTHORITY / AUTH__ZITADEL__AUDIENCE), resolved from DI the same way
        // Flow.Silo/Program.cs's ConfigureBlobStorage/ConfigureClustering resolve their own options
        // dependencies (an OptionsBuilder<TOptions>.Configure<TDep> overload, not a hand-rolled
        // IConfiguration read) - consistent house style for "config-shaped, DI-resolved" options.
        private static void AddAuthentication(IServiceCollection services)
        {
            services
                .AddOptions<ZitadelAuthOptions>()
                .BindConfiguration(ZitadelAuthOptions.ConfigKey);

            services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer();

            services
                .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<IOptionsMonitor<ZitadelAuthOptions>>((jwt, zitadelMonitor) =>
                {
                    var options = zitadelMonitor.CurrentValue;

                    // Keeps the token's "sub" claim literally named "sub" (no legacy
                    // ClaimTypes.NameIdentifier remap) - IdentityContextMiddleware reads it as-is.
                    jwt.MapInboundClaims = false;

                    jwt.Authority = options.Authority;
                    jwt.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = options.Authority,
                        ValidateAudience = true,
                        ValidAudience = options.Audience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                    };
                });
        }
    }
}
