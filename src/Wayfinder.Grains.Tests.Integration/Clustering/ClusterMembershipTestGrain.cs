using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace Flow.Grains.Tests.Integration.Clustering
{
    public interface IClusterMembershipTestGrain : IGrainWithIntegerKey
    {
        // Returns the address of whichever silo actually activated this grain - proof a real
        // grain call was served by a live silo somewhere in the cluster (not merely that the
        // client constructed a local proxy), independent of which silo answers.
        Task<string> GetSiloAddress();
    }

    // Deliberately minimal and CMMN-free: plain Grain (no persistent state), no reminders, no
    // streams - this suite exists purely to prove the CLUSTERING path (work item #30's
    // ConfigureDeployedOrleans / UseAzureStorageClustering), not any storage/stream provider.
    public class ClusterMembershipTestGrain : Grain, IClusterMembershipTestGrain
    {
        private readonly ILocalSiloDetails _localSiloDetails;

        public ClusterMembershipTestGrain(ILocalSiloDetails localSiloDetails)
        {
            _localSiloDetails = localSiloDetails;
        }

        public Task<string> GetSiloAddress() => Task.FromResult(_localSiloDetails.SiloAddress.ToString());
    }
}
