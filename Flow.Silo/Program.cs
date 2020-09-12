using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using AutoMapper;
using Flow.Grains.Infrastructure.AutoMapper;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Infrastructure.Quartz;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Interfaces.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;
using Flow.Silo.Infrastructure.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Exceptions;
using HostBuilderContext = Microsoft.Extensions.Hosting.HostBuilderContext;

namespace Flow.Silo
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            await CreateHostBuilder(args).Build().RunAsync();
        }

        private static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                })
                .ConfigureLogging(ConfigureLogging)
                .UseOrleans(ConfigureOrleans)
                .ConfigureServices(ConfigureServices)
                .UseConsoleLifetime();
        
        private static void ConfigureLogging(HostBuilderContext context, ILoggingBuilder logging)
        {
            var seqOptions = context.Configuration.GetSection(SeqOptions.ConfigKey).Get<SeqOptions>();

            var levelSwitch = new LoggingLevelSwitch
            {
                MinimumLevel = LogEventLevel.Debug
            };

            logging.AddSerilog(new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitch)
                .Enrich.FromLogContext()
                .Enrich.WithExceptionDetails()
                .WriteTo.Seq(
                    serverUrl: seqOptions.ServerUrl,
                    apiKey: seqOptions.ApiKey,
                    controlLevelSwitch: levelSwitch
                )
                .CreateLogger());
        }

        private static void ConfigureOrleans(HostBuilderContext context, ISiloBuilder silo)
        {
            if (context.HostingEnvironment.IsDevelopment())
            {
                ConfigureDevelopmentOrleans(silo);
            }
            else
            {
                ConfigureDeployedOrleans(context, silo);
            }

            silo
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = context.HostingEnvironment.EnvironmentName;
                    options.ServiceId = context.HostingEnvironment.ApplicationName;
                })
                .ConfigureApplicationParts(parts => parts
                    .AddApplicationPart(typeof(IPlanItemInternalGrain).Assembly)
                    .WithReferences())
                .UseSiloUnobservedExceptionsHandler();
        }

        private static void ConfigureDevelopmentOrleans(ISiloBuilder silo)
        {
            silo
                .UseLocalhostClustering()

                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)

                .AddMemoryGrainStorageAsDefault() // grain state
                .AddLogStorageBasedLogConsistencyProvider() // journaled grain
                .AddMemoryGrainStorage("PubSubStore") // stream storage
                .AddSimpleMessageStreamProvider("Default", options => options.FireAndForgetDelivery = true) // cluster stream provider
                .UseInMemoryReminderService();
        }

        private static void ConfigureDeployedOrleans(HostBuilderContext context, ISiloBuilder silo)
        {
            throw new NotImplementedException();
        }

        private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
        {
            services
                .AddRuleExecutor()
                .AddSingleton<IPlanItemBehaviorConfigurator, PlanItemBehaviorConfiguratorService>()
                .AddSingleton<IPlanItemStateMachineConfigurator, PlanItemStateMachineConfiguratorService>()
                .AddAutoMapper(cfg => cfg.AddProfile<CaseFlowProfile>(), Enumerable.Empty<Assembly>())
                .AddQuartz(QuartzSchedulerConfig.Volatile);
        }
    }
}
