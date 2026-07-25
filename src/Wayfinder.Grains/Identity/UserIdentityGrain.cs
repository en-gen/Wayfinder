using System;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Identity;
using Orleans;
using Orleans.Runtime;

namespace Wayfinder.Grains.Identity
{
    // ADO #33 - state for IUserIdentityGrain. A plain [GenerateSerializer] class (not a
    // JournaledGrain event/view pair) - the registry is reference/config data seeded explicitly
    // (IdentityRegistrySeeder, or a test/admin caller), not an evented aggregate that replays a
    // history. Registered defaults false so a never-called grain (the common case: most subject
    // strings will never exist) answers TryResolve() with null instead of a zeroed-out Guid
    // identity that looks superficially valid.
    [GenerateSerializer]
    public class UserIdentityGrainState
    {
        [Id(0)]
        public bool Registered { get; set; }

        [Id(1)]
        public Guid TenantId { get; set; }

        [Id(2)]
        public Guid UserId { get; set; }

        [Id(3)]
        public string[] Roles { get; set; } = Array.Empty<string>();
    }

    // ADO #33 - the tenant registry's subject -> identity grain. Backed by
    // [PersistentState(_, "Default")] against whatever the host registered as its DEFAULT grain
    // storage (AddAzureBlobGrainStorageAsDefault in Wayfinder.Silo/Program.cs;
    // AddMemoryGrainStorageAsDefault in ClusterFixture's TestSiloConfigurator) - the SAME storage
    // slot CmmnElementGrain's journaled state ultimately lands in
    // (see Program.cs's ConfigureSharedOrleansProviders remarks for why "Default" is the right
    // provider name), just reached the plain [PersistentState] way instead of the
    // [LogConsistencyProvider("LogStorage")] way. State here is small primitives/arrays only - no
    // System.Text.Json.Nodes - so the default storage's GrainStorageSerializer pin (Orleans's
    // native serializer, see Program.cs) round-trips it with no special handling.
    public class UserIdentityGrain : Grain, IUserIdentityGrain
    {
        private readonly IPersistentState<UserIdentityGrainState> _state;

        public UserIdentityGrain(
            [PersistentState("userIdentity", "Default")] IPersistentState<UserIdentityGrainState> state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        public async Task Register(UserIdentityRecord identity)
        {
            if (identity is null) throw new ArgumentNullException(nameof(identity));

            _state.State.Registered = true;
            _state.State.TenantId = identity.TenantId;
            _state.State.UserId = identity.UserId;
            _state.State.Roles = identity.Roles ?? Array.Empty<string>();

            await _state.WriteStateAsync();
        }

        public Task<UserIdentityRecord> TryResolve() =>
            Task.FromResult(_state.State.Registered
                ? new UserIdentityRecord
                {
                    TenantId = _state.State.TenantId,
                    UserId = _state.State.UserId,
                    Roles = _state.State.Roles
                }
                : null);
    }
}
