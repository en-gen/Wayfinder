using System;
using Flow.Grains.Executables;
using Jint;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flow.Grains.Infrastructure.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddRuleExecutor(this IServiceCollection services) =>
            services
                .AddTransient<Engine>()
                .AddSingleton<Func<string, IExecutable>>(sp =>
                    expression =>
                        new Executable(
                            sp.GetRequiredService<Engine>(),
                            sp.GetRequiredService<ILogger<Executable>>(),
                            expression));
    }
}
