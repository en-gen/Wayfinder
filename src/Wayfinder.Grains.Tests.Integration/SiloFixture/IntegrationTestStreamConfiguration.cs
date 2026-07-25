using System;
using Orleans.Hosting;

namespace Wayfinder.Grains.Tests.Integration.SiloFixture
{
    // Shared memory-stream pulling-agent tuning for the in-memory TestCluster fixtures
    // (ClusterFixture and RepetitionGuardClusterFixture - kept identical between the two per
    // their own "mirrors" comments). Issue #153: Orleans' default pulling-agent poll
    // (StreamPullingAgentOptions.GetQueueMsgsTimerPeriod, ~100ms) means every memory-stream hop
    // pays at least that long before a message is picked up. Grain-to-grain cascades that chain
    // several stream hops sequentially (e.g. the RepetitionGuardFootgun spawn->complete->
    // re-spawn->breach->fault cascade in RepetitionGuardClusterFixture) multiply that latency by
    // the chain length, and the wait balloons further under CI thread-pool contention - eating
    // into fixed test poll budgets. Polling every 15ms instead cuts both the steady-state
    // latency and its CI variance; it is a stream-provider timing knob only, it does not change
    // any delivery guarantee or guard/fault behavior. Silo-side only - pulling agents run on the
    // silo, so the client-side AddMemoryStreams registrations are left untouched.
    internal static class IntegrationTestStreamConfiguration
    {
        public static void Configure(ISiloMemoryStreamConfigurator configurator) =>
            configurator.ConfigurePullingAgent(ob => ob.Configure(options =>
                options.GetQueueMsgsTimerPeriod = TimeSpan.FromMilliseconds(15)));
    }
}
