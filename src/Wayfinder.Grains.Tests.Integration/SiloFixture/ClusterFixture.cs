using System;
using System.Net;
using System.Threading.Tasks;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Infrastructure.Quartz;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Services.PlanItemBehaviorConfigurator;
using Wayfinder.Grains.Services.PlanItemStateMachineConfigurator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Extensions;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using Serilog;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.SiloFixture
{
    public class ClusterFixture : IDisposable, IAsyncLifetime
    {
        public TestCluster Cluster { get; private set; }
        public IClusterClient ClusterClient { get; private set; }

        private bool _disposed = false;

        public Task InitializeAsync()
        {
            var builder = new TestClusterBuilder();

            builder.Options.ClusterId = "integration";
            builder.Options.ServiceId = "Case.Flow";

            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
            builder.AddClientBuilderConfigurator<TestClientConfigurator>();

            Cluster = builder.Build();
            Cluster.Deploy();

            ClusterClient = Cluster.Client;

            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            Cluster.StopAllSilos();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                Log.CloseAndFlush();
            }

            _disposed = true;
        }

        // Mirrors Wayfinder.Silo/Program.cs's ConfigureDevelopmentOrleans + serializer fallback, adapted
        // to the TestCluster's class-based ISiloConfigurator (TestClusterBuilder has no delegate
        // overload equivalent to ISiloHostBuilder's old ConfigureServices(HostBuilderContext, ...)).
        private class TestSiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder silo)
            {
                silo
                    .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)

                    .AddMemoryGrainStorageAsDefault(ConfigureMemoryStorage) // grain state
                    .AddLogStorageBasedLogConsistencyProvider() // journaled grain
                    .AddMemoryGrainStorage("PubSubStore", ConfigureMemoryStorage) // stream storage
                    .AddMemoryStreams("Default", ConfigureMemoryStreamsPullingAgent) // cluster stream provider
                    .UseInMemoryReminderService()

                    .ConfigureServices(ConfigureServices)
                    .ConfigureLogging(IntegrationTestLogging.Configure);

                silo.Services.AddSerializer(s => s.AddJsonSerializer(
                    isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                    jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));

                // ADO #33 - see Wayfinder.Silo/Program.cs's ConfigureSharedOrleansProviders remarks:
                // Orleans's built-in ExceptionCodec only allows exception types whose namespace
                // matches ExceptionSerializationOptions.SupportedNamespacePrefixes (defaults:
                // "System"/"Microsoft"/"Azure"), so custom exceptions like CrossTenantAccessException
                // need "Wayfinder" allow-listed here too - must match the production silo and the client
                // configurator below identically.
                silo.Services.Configure<ExceptionSerializationOptions>(
                    options => options.SupportedNamespacePrefixes.Add("Wayfinder"));
            }

            // MemoryGrainStorage's default IGrainStorageSerializer is JsonGrainStorageSerializer -
            // a reflection-based JSON storage serializer that is a SEPARATE stack from the Orleans
            // wire serializer (and from the fallback JSON codec registered above). It cannot
            // round-trip System.Text.Json.Nodes values held in grain state: journaling a
            // CaseFileItemStore whose Value contains a JsonArray fails inside the storage write,
            // which the log-consistency protocol (LogViewAdaptor) retries indefinitely - the grain
            // call never completes (observed as a client-side 30s timeout with the activation
            // spinning at ~6k scheduler work items/sec; see work item #16). Pinning the storage
            // serializer to OrleansGrainStorageSerializer routes grain-state persistence through
            // the same Orleans serializer used on the wire, which handles the JsonNode family
            // natively (see JsonNodeOrleansSerializationTests). Must match Wayfinder.Silo/Program.cs's
            // development configuration identically.
            private static void ConfigureMemoryStorage(OptionsBuilder<MemoryGrainStorageOptions> options) =>
                options.Configure<Serializer>((storageOptions, serializer) =>
                    storageOptions.GrainStorageSerializer = new OrleansGrainStorageSerializer(serializer));

            // Issue #153: Orleans' default pulling-agent poll (StreamPullingAgentOptions.
            // GetQueueMsgsTimerPeriod, ~100ms) means every memory-stream hop pays at least that
            // long before a message is picked up. Grain-to-grain cascades that chain several
            // stream hops sequentially (e.g. the RepetitionGuardFootgun spawn->complete->
            // re-spawn->breach->fault cascade in RepetitionGuardClusterFixture) multiply that
            // latency by the chain length, and the wait balloons further under CI thread-pool
            // contention - eating into fixed test poll budgets. Polling every 15ms instead cuts
            // both the steady-state latency and its CI variance; it is a stream-provider timing
            // knob only, it does not change any delivery guarantee or guard/fault behavior.
            private static void ConfigureMemoryStreamsPullingAgent(ISiloMemoryStreamConfigurator configurator) =>
                configurator.ConfigurePullingAgent(ob => ob.Configure(options =>
                    options.GetQueueMsgsTimerPeriod = TimeSpan.FromMilliseconds(15)));

            private static void ConfigureServices(IServiceCollection services)
            {
                services
                    .AddSingleton<IClock>(SystemClock.Instance.InUtc())
                    .AddRuleExecutor()
                    .AddSingleton<IPlanItemBehaviorConfigurator, PlanItemBehaviorConfiguratorService>()
                    .AddSingleton<IPlanItemStateMachineConfigurator, PlanItemStateMachineConfiguratorService>()
                    .AddQuartz(QuartzSchedulerConfig.Volatile);
            }

        }

        // TestCluster's in-process client independently validates serializer coverage for every
        // type reachable from grain interfaces, so it needs the same fallback registration as the
        // silo above - without this, client-side calls that touch CMMN model types throw
        // CodecNotFoundException even though the silo itself is configured correctly.
        private class TestClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.AddMemoryStreams("Default");

                clientBuilder.Services.AddSerializer(s => s.AddJsonSerializer(
                    isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                    jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));

                // ADO #33 - must match TestSiloConfigurator/Wayfinder.Silo/Program.cs identically (see
                // those remarks): without this, a foreign-tenant call throwing
                // CrossTenantAccessException fails client-side with CodecNotFoundException instead
                // of surfacing the exception the grain actually threw.
                clientBuilder.Services.Configure<ExceptionSerializationOptions>(
                    options => options.SupportedNamespacePrefixes.Add("Wayfinder"));
            }
        }
    }
}
