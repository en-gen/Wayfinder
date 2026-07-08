using System;
using System.Net;
using System.Threading.Tasks;
using Flow.Grains.Infrastructure.Extensions;
using Flow.Grains.Infrastructure.Quartz;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Services.PlanItemBehaviorConfigurator;
using Flow.Grains.Services.PlanItemStateMachineConfigurator;
using Flow.Silo.Infrastructure.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Serialization;
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
                });

            // Fallback serializer for types Orleans's [GenerateSerializer] codegen can't reasonably
            // cover: the XSD-generated CMMN model (Flow.Grains.Interfaces.Model). Everything else in
            // the solution is swept with [GenerateSerializer] + [Id(n)] and uses the native serializer.
            // See OrleansFallbackJsonSerializer for the shared isSupported predicate/options - the
            // TestCluster silo and client configurators in ClusterFixture must register identically.
            silo.Services.AddSerializer(s => s.AddJsonSerializer(
                isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));
        }

        private static void ConfigureDevelopmentOrleans(ISiloBuilder silo)
        {
            silo
                .UseLocalhostClustering()

                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)

                .AddMemoryGrainStorageAsDefault() // grain state
                .AddLogStorageBasedLogConsistencyProvider() // journaled grain
                .AddMemoryGrainStorage("PubSubStore") // stream storage
                .AddMemoryStreams("Default") // cluster stream provider (replaces removed AddSimpleMessageStreamProvider)
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
                .AddQuartz(QuartzSchedulerConfig.Volatile);
        }
    }
}
