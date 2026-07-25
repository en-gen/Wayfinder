using System;
using System.Threading.Tasks;
using Flow.Grains.Interfaces.Identity;
using Orleans;
using Orleans.Runtime;

namespace Flow.Grains.Identity
{
    // ADO #33 - state for ITenantGrain. Same reasoning as UserIdentityGrainState: plain
    // reference/config data, not an evented aggregate.
    [GenerateSerializer]
    public class TenantGrainState
    {
        [Id(0)]
        public bool Seeded { get; set; }

        [Id(1)]
        public string Name { get; set; }

        [Id(2)]
        public TenantOidcConfig Oidc { get; set; }
    }

    // ADO #33 - the tenant registry's per-tenant metadata/OIDC-config grain, keyed by the tenant's
    // own Guid. Same storage story as UserIdentityGrain - see that file's remarks.
    public class TenantGrain : Grain, ITenantGrain
    {
        private readonly IPersistentState<TenantGrainState> _state;

        public TenantGrain(
            [PersistentState("tenant", "Default")] IPersistentState<TenantGrainState> state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        public async Task Seed(string name, TenantOidcConfig oidc)
        {
            _state.State.Seeded = true;
            _state.State.Name = name;
            _state.State.Oidc = oidc;

            await _state.WriteStateAsync();
        }

        public Task<TenantRecord> Get() =>
            Task.FromResult(_state.State.Seeded
                ? new TenantRecord
                {
                    TenantId = this.GetPrimaryKey(),
                    Name = _state.State.Name,
                    Oidc = _state.State.Oidc
                }
                : null);
    }
}
