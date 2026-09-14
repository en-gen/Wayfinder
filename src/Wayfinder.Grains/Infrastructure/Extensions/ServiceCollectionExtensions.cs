using System;
using System.Collections.Specialized;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Expressions;
using Wayfinder.Grains.Infrastructure.Quartz;
using Wayfinder.Grains.Scheduler;
using Jint;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Spi;

namespace Wayfinder.Grains.Infrastructure.Extensions
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
                            expression))
                // OQ-9 (design 05 section D.2, measured in #257): the expression evaluator is an
                // in-process singleton, not a grain. A grain hop cost 5.8-6.9 us against 8-10 ns
                // for an in-process call, which is more than the Jint work it wrapped.
                .AddSingleton<IExpressionEvaluator, ExpressionEvaluator>();

        public static IServiceCollection AddQuartz(this IServiceCollection services, NameValueCollection config) =>
            services
                .AddTransient<TimerTickJob>()
                .AddSingleton<IJobFactory, QuartzJobFactory>()
                .AddSingleton<ISchedulerFactory, QuartzSchedulerFactory>()
                .AddSingleton(config);
    }
}
