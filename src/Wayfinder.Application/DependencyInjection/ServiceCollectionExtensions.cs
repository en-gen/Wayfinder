using System;
using System.Linq;
using Wayfinder.Application.Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Wayfinder.Application.DependencyInjection
{
    // ADO #32 - wires the native mediator into any host's IServiceCollection. A small reflection
    // scan over THIS assembly only - deliberately NOT Scrutor or any other third-party assembly
    // scanner, per ADO #32's scope fence ("everything native").
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddFlowApplication(this IServiceCollection services)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));

            // Scoped: a future ASP.NET host resolves one Sender per request; a test composes one
            // root provider and gets one Sender for the life of that provider either way.
            services.AddScoped<ISender, Sender>();

            // ADO #33 - the tenant-registry resolver/seeder, additive: nothing calls ITenantResolver
            // yet (sub-unit 3 wires it into an HTTP auth middleware), so this only grows the DI
            // container's service list - it does not change AddFlowApplication's existing behavior
            // for any of today's callers (CaseCqrsIntegrationTests; a future Wayfinder.Silo host).
            services.AddFlowIdentity();

            var handlerInterfaceDefinitions = new[] { typeof(ICommandHandler<,>), typeof(IQueryHandler<,>) };
            var assembly = typeof(ServiceCollectionExtensions).Assembly;

            var registrations =
                from implementationType in assembly.GetTypes()
                where implementationType.IsClass && !implementationType.IsAbstract
                from interfaceType in implementationType.GetInterfaces()
                where interfaceType.IsGenericType
                where handlerInterfaceDefinitions.Contains(interfaceType.GetGenericTypeDefinition())
                select (interfaceType, implementationType);

            foreach (var (interfaceType, implementationType) in registrations)
            {
                services.AddTransient(interfaceType, implementationType);
            }

            return services;
        }
    }
}
