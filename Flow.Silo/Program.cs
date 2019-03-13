using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AutoMapper;
using Flow.Grains.Infrastructure.AutoMapper;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Infrastructure.Quartz;
using Flow.Grains.Interfaces.Plan.Case;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Extensions;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Exceptions;

namespace Flow.Silo
{
    public class Program
    {
        private static ISiloHost SiloHost { get; set; }

        public async Task Main()
        {
            var levelSwitch = new LoggingLevelSwitch
            {
                MinimumLevel = LogEventLevel.Debug
            };
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(levelSwitch)
                .Enrich.FromLogContext()
                .Enrich.WithExceptionDetails()
                .WriteTo.Seq(
                    "http://localhost:5341",
                    controlLevelSwitch: levelSwitch
                )
                .CreateLogger();

            Log.Logger.Debug("initializing silo");

            try
            {
                SiloHost = new SiloHostBuilder()
                    .ConfigureLogging(builder => builder.AddSerilog(Log.Logger))
                    .ConfigureServices(ConfigureServices)
                    
                    .UseAzureStorageClustering(options => options.ConnectionString = "UseDevelopmentStorage=true")
                    // .UseLocalhostClustering()

                    // .AddMemoryGrainStorageAsDefault()
                    .AddAzureTableGrainStorageAsDefault() // grain state
                    .AddLogStorageBasedLogConsistencyProvider() // journaled grain

                    .AddAzureTableGrainStorage("PubSubStore")
                    // .AddMemoryGrainStorage("PubSubStore") // stream storage
                    
                    // .AddAzureQueueStreams("Default", options => options.)
                    .AddSimpleMessageStreamProvider("Default", options => options.FireAndForgetDelivery = true) // cluster stream provider
                    // .UseInMemoryReminderService()
                    .UseAzureTableReminderService("UseDevelopmentStorage=true")

                    .ConfigureApplicationParts(parts => parts
                        .AddApplicationPart(typeof(IPlanItemGrain).Assembly) // Flow.Grains
                        .AddApplicationPart(typeof(ICaseGrain).Assembly)) //Flow.Grains.Interfaces

                    .UseSiloUnobservedExceptionsHandler()

                    .Build();

                await SiloHost.StartAsync();
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "failure during cluster startup");
            }
        }


        private static void ConfigureServices(HostBuilderContext ctx, IServiceCollection services)
        {
            services
                //.AddSingleton(sp => new ClientBuilder()
                //    .UseLocalhostClustering()

                //    .AddSimpleMessageStreamProvider("Default")

                //    .ConfigureApplicationParts(parts => parts
                //        .AddApplicationPart(typeof(IPlanItemGrain).Assembly)
                //        .AddApplicationPart(typeof(ICaseGrain).Assembly))

                //    .Build())
                .AddSingleton<IClock>(SystemClock.Instance.InUtc())
                .AddRuleExecutor()
                .AddSingleton<IPlanItemBehaviorConfigurator, PlanItemBehaviorConfiguratorService>()
                .AddSingleton<IPlanItemStateMachineConfigurator, PlanItemStateMachineConfiguratorService>()
                .AddAutoMapper(cfg => cfg.AddProfile<CaseFlowProfile>(), Enumerable.Empty<Assembly>())
                .AddQuartz(QuartzSchedulerConfig.Volatile);
        }
    }
}
