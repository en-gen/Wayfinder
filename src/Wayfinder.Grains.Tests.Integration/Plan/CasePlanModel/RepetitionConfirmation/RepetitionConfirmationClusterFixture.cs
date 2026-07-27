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
using Orleans.Runtime.Hosting;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using Serilog;
using Xunit;
using Wayfinder.Grains.Tests.Integration.SiloFixture;

namespace Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel.RepetitionConfirmation
{
    // Issue #160 - a dedicated, isolated TestCluster (in-memory, no containers - as cheap as
    // SiloFixture.ClusterFixture), mirroring RepetitionGuardClusterFixture's rationale for why it
    // is NOT the shared ClusterFixture/ClusterCollection: RepetitionChildConfirmationIntegrationTests
    // calls IManagementGrain.ForceActivationCollection(TimeSpan.Zero), which deactivates every
    // activation in the ENTIRE cluster it runs against. On the shared cluster (reused by dozens of
    // other integration tests across the whole suite), that forced sweep - and the unrelated
    // background stream/grain activity contending for the same in-process scheduler - was found
    // empirically to make the race this test targets (does the parent's ConfirmEvents complete
    // before the forced deactivation lands) non-deterministic: passing reliably alone, but flaking
    // under the full suite's load in either direction. A dedicated, otherwise-idle cluster removes
    // that contention, matching this suite's own established pattern for tests whose timing
    // assertions need isolation from the rest of the collection.
    public class RepetitionConfirmationClusterFixture : IDisposable, IAsyncLifetime
    {
        public TestCluster Cluster { get; private set; }
        public IClusterClient ClusterClient { get; private set; }

        private bool _disposed = false;

        public Task InitializeAsync()
        {
            var builder = new TestClusterBuilder();

            builder.Options.ClusterId = "repetition-confirmation-integration";
            builder.Options.ServiceId = "Wayfinder";

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

        // Mirrors SiloFixture.ClusterFixture.TestSiloConfigurator byte-for-byte - see there for
        // the full rationale on each registration. No configuration differs; only the isolation
        // (a private TestCluster instance) matters here.
        private class TestSiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder silo)
            {
                silo
                    .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)

                    .AddLogStorageBasedLogConsistencyProvider() // journaled grain
                    .AddMemoryGrainStorage("PubSubStore", ConfigureMemoryStorage) // stream storage
                    .AddMemoryStreams("Default") // cluster stream provider
                    .UseInMemoryReminderService()

                    .ConfigureServices(ConfigureServices)
                    .ConfigureLogging(IntegrationTestLogging.Configure);

                // #160 - deliberately NOT AddMemoryGrainStorageAsDefault: see
                // DelayedInMemoryGrainStorage's remarks for why RepetitionChildConfirmation
                // IntegrationTests needs the "Default" grain-state write path stretched out to a
                // fixed, generous delay to make its forced-deactivation race deterministic. Only
                // "Default" (the journaled grains' own state) is affected; PubSubStore above
                // (stream subscription bookkeeping) stays on regular fast memory storage.
                silo.Services.AddGrainStorage("Default", (_, _) => new DelayedInMemoryGrainStorage());

                silo.Services.AddSerializer(s => s.AddJsonSerializer(
                    isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                    jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));

                silo.Services.Configure<ExceptionSerializationOptions>(
                    options => options.SupportedNamespacePrefixes.Add("Wayfinder"));
            }

            private static void ConfigureMemoryStorage(OptionsBuilder<MemoryGrainStorageOptions> options) =>
                options.Configure<Serializer>((storageOptions, serializer) =>
                    storageOptions.GrainStorageSerializer = new OrleansGrainStorageSerializer(serializer));

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

        private class TestClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.AddMemoryStreams("Default");

                clientBuilder.Services.AddSerializer(s => s.AddJsonSerializer(
                    isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                    jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));

                clientBuilder.Services.Configure<ExceptionSerializationOptions>(
                    options => options.SupportedNamespacePrefixes.Add("Wayfinder"));
            }
        }
    }
}
