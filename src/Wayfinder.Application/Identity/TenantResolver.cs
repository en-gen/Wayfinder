using System;
using System.Threading;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Identity;
using Orleans;

namespace Flow.Application.Identity
{
    // ADO #33 - default ITenantResolver: calls IUserIdentityGrain(subject) exactly like the
    // Unit-1 command/query handlers call their grains (constructor-injected IClusterClient - see
    // CreateCaseCommandHandler/GetCaseQueryHandler). An unknown subject never throws - the grain
    // itself never throws (IUserIdentityGrain.TryResolve's contract), so the only "not resolved"
    // path here is a null record or a blank subject.
    public sealed class TenantResolver : ITenantResolver
    {
        private readonly IClusterClient _clusterClient;

        public TenantResolver(IClusterClient clusterClient)
        {
            _clusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));
        }

        public async Task<ResolvedIdentity> ResolveAsync(
            string subject, string issuer = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                return ResolvedIdentity.NotResolved;
            }

            var userIdentityGrain = _clusterClient.GetGrain<IUserIdentityGrain>(subject);
            var record = await userIdentityGrain.TryResolve();

            return record is null
                ? ResolvedIdentity.NotResolved
                : ResolvedIdentity.Found(record.TenantId, record.UserId, record.Roles);
        }
    }
}
