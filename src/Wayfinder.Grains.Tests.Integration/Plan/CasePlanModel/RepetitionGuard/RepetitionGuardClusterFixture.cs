using System;
using System.Net;
using System.Threading.Tasks;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Infrastructure.Quartz;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Services.PlanItemBehaviorConfigurator;
using Wayfinder.Grains.Services.PlanItemStateMachineConfigurator;
using Wayfinder.Grains.Tests.Utils.Helpers;
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
using Wayfinder.Grains.Tests.Integration.SiloFixture;

namespace Wayfinder.Grains.Tests.Integration.Plan.CasePlanModel.RepetitionGuard
{
    // ADO #67 - a dedicated, isolated TestCluster (in-memory, no containers - as cheap as
    // SiloFixture.ClusterFixture) that pins RepetitionGuardOptions.MaxRepetitionsPerPlanItem to a
    // small value instead of the generous engine default. Deliberately NOT the shared
    // ClusterFixture/ClusterCollection: that singleton is reused by every other integration test
    // (including RepetitionOnCompletionIntegrationTests/SentryRepetitionResetIntegrationTests,
    // whose low repeat counts must keep exercising the real generous default, unperturbed), and
    // IPlanItemBehaviorConfigurator resolves its ceiling once via constructor injection - there is
    // no per-test override mechanism within a single shared cluster. Mirrors
    // SiloFixture.ClusterFixture byte-for-byte except the one added Configure<RepetitionGuardOptions>
    // call and a distinct ClusterId (avoids any ambiguity with the shared cluster, though
    // TestCluster instances do not actually collide).
    public class RepetitionGuardClusterFixture : IDisposable, IAsyncLifetime
    {
        // Small enough to assert on deterministically and quickly, large enough to distinguish
        // "the ceiling fired" from "something else stopped it after one repetition".
        public const int LowCeiling = 5;

        public TestCluster Cluster { get; private set; }
        public IClusterClient ClusterClient { get; private set; }

        private bool _disposed = false;

        public async Task InitializeAsync()
        {
            var builder = new TestClusterBuilder();

            builder.Options.ClusterId = "repetition-guard-integration";
            builder.Options.ServiceId = "Case.Flow";

            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
            builder.AddClientBuilderConfigurator<TestClientConfigurator>();

            Cluster = builder.Build();
            Cluster.Deploy();

            ClusterClient = Cluster.Client;

            // Issue #153: this fixture is the sole test in its own TestCluster (see the class
            // remarks on why it can't share ClusterFixture/ClusterCollection), so unlike every
            // other integration test it never gets the first-hit JIT/serializer/streaming-path
            // warm-up amortized across other tests in the collection - the timed test below would
            // otherwise pay that cold-start cost on top of the O(ceiling) sequential stream hops
            // it is already measuring, on every single run. Run the exact same spawn->complete->
            // re-spawn->breach->fault cascade once here, through a throwaway case (its own tenant,
            // definition id and instance id - never referenced by the real test, so there is no
            // shared-state or id-collision risk), so those first-hit costs are already paid before
            // the timed assertion starts. Best-effort: if the warm-up itself fails or times out,
            // swallow it rather than fail fixture setup over what is purely a priming step - the
            // real test still exercises and asserts on the cascade regardless.
            try
            {
                await WarmUpRepetitionCascadeAsync();
            }
            catch
            {
                // Best-effort warm-up only - see remarks above.
            }
        }

        // See the warm-up remarks in InitializeAsync above. Deliberately mirrors the shape of
        // RepetitionGuardFootgunIntegrationTests's own case (same foot-gun: non-blocking task,
        // RepetitionRule=TRUE, ManualActivationRule=FALSE, no entry criteria, blocking sentinel
        // sibling) so it exercises the identical behavior/streaming/serializer code paths, but
        // with entirely distinct ids so it can never interact with the real test's state.
        private async Task WarmUpRepetitionCascadeAsync()
        {
            const string scope = "WarmUpCPM";
            const string taskDefinitionId = "WarmUpSourceTask";
            const string taskPlanItemId = "WarmUpPlanItemSource";
            const string sentinelDefinitionId = "WarmUpSentinelTask";
            const string sentinelPlanItemId = "WarmUpPlanItemSentinel";

            CaseRequestContext.TenantId = Guid.Parse("00000000-0000-0000-0000-0000000000ff");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-0000000000fe");

            var caseInstanceId = Guid.NewGuid();
            var caseDefinitionId = $"warmup-case-{Guid.NewGuid()}";

            var taskDefinition = new HumanTask { Id = taskDefinitionId, IsBlocking = false };
            var sentinelDefinition = new HumanTask { Id = sentinelDefinitionId, IsBlocking = true };

            var @case = new Case
            {
                Id = caseDefinitionId,
                CaseRoles = new CaseRoles(),
                CasePlanModel = new Stage
                {
                    Id = scope,
                    PlanItemDefinitions = { taskDefinition, sentinelDefinition },
                    PlanItems =
                    {
                        // Wayfinder.Grains.Tests.Integration.Plan.PlanItem (see
                        // Plan/PlanItem/PlanItemGrainTests.cs) shares an enclosing namespace with
                        // this fixture's own Wayfinder.Grains.Tests.Integration.Plan.* namespace
                        // and shadows the imported Interfaces.Model.PlanItem model type, exactly
                        // as in RepetitionGuardFootgunIntegrationTests - fully qualify to
                        // disambiguate, matching that file's own convention.
                        new Interfaces.Model.PlanItem
                        {
                            Id = taskPlanItemId,
                            DefinitionRef = taskDefinition.Id,
                            ItemControl = new PlanItemControl
                            {
                                RepetitionRule = Rules.IsRepeatableRule,
                                ManualActivationRule = Rules.NotManuallyActivated
                            }
                        },
                        new Interfaces.Model.PlanItem
                        {
                            Id = sentinelPlanItemId,
                            DefinitionRef = sentinelDefinition.Id
                        }
                    }
                }
            };

            await ClusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = ClusterClient.GetGrain<ICaseGrain>(caseInstanceId, scope);
            await caseGrain.Create(caseDefinitionId);
            await caseGrain.Trigger(PlanItemTransition.Create);

            // Bounded, generous wait - this only needs to pay the first-hit cost, not prove the
            // guard fires correctly (the real test already asserts that). If it doesn't settle in
            // time, just move on: the outer try/catch has nothing to catch here, so simply
            // returning is enough to let the real test proceed (cold, at worst - never broken).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var snapshot = await caseGrain.GetSnapshot();
                if (snapshot.PlanItemState == PlanItemState.Failed) return;
                await Task.Delay(TimeSpan.FromMilliseconds(20));
            }
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

        // Mirrors SiloFixture.ClusterFixture.TestSiloConfigurator - see there for the full
        // rationale on each registration. The only substantive addition is the
        // Configure<RepetitionGuardOptions> call in ConfigureServices below.
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
            }

            private static void ConfigureMemoryStorage(OptionsBuilder<MemoryGrainStorageOptions> options) =>
                options.Configure<Serializer>((storageOptions, serializer) =>
                    storageOptions.GrainStorageSerializer = new OrleansGrainStorageSerializer(serializer));

            // Issue #153 - see SiloFixture.ClusterFixture.ConfigureMemoryStreamsPullingAgent for
            // the full rationale: this fixture's flagship test IS the O(ceiling) sequential
            // stream-hop cascade, so it is the direct beneficiary of polling faster than Orleans'
            // ~100ms default.
            private static void ConfigureMemoryStreamsPullingAgent(ISiloMemoryStreamConfigurator configurator) =>
                configurator.ConfigurePullingAgent(ob => ob.Configure(options =>
                    options.GetQueueMsgsTimerPeriod = TimeSpan.FromMilliseconds(15)));

            private static void ConfigureServices(IServiceCollection services)
            {
                services
                    .AddSingleton<IClock>(SystemClock.Instance.InUtc())
                    .AddRuleExecutor()
                    // ADO #67 - the one deliberate difference from SiloFixture.ClusterFixture:
                    // pins the repetition ceiling low so the foot-gun scenario (#19: non-blocking
                    // Task, ManualActivationRule=false, RepetitionRule=TRUE, no entry criteria)
                    // halts at a small, assertable count instead of spawning until the generous
                    // production default (10,000).
                    .Configure<RepetitionGuardOptions>(o => o.MaxRepetitionsPerPlanItem = LowCeiling)
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
            }
        }
    }
}
