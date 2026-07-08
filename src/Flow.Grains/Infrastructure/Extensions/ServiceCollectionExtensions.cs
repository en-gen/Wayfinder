using System;
using System.Collections.Specialized;
using Flow.Grains.Executables;
using Flow.Grains.Infrastructure.Quartz;
using Flow.Grains.Scheduler;
using Jint;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Spi;

namespace Flow.Grains.Infrastructure.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddRuleExecutor(this IServiceCollection services) =>
            services
                .AddTransient(_ => SandboxedJintEngine.Create())
                .AddSingleton<Func<string, IExecutable>>(sp =>
                    expression =>
                        new Executable(
                            sp.GetRequiredService<Engine>(),
                            sp.GetRequiredService<ILogger<Executable>>(),
                            expression));

        public static IServiceCollection AddQuartz(this IServiceCollection services, NameValueCollection config) =>
            services
                .AddTransient<TimerTickJob>()
                .AddSingleton<IJobFactory, QuartzJobFactory>()
                .AddSingleton<ISchedulerFactory, QuartzSchedulerFactory>()
                .AddSingleton(config);
    }
}
