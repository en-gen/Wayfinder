using System;
using System.Threading.Tasks;
using Wayfinder.Application.DependencyInjection;
using Wayfinder.Application.Identity;
using Wayfinder.Grains.Interfaces.Identity;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Identity
{
    // ADO #33 - proves the tenant-registry seam sub-unit 3's HTTP auth middleware will call:
    // seed two independent tenants (each with a user + roles), resolve each subject back to the
    // correct (TenantId, UserId, Roles), confirm an unknown subject never resolves (and never
    // throws), and confirm both registry grains persist across a forced deactivation/reactivation
    // - i.e. they are genuinely backed by the default grain storage, not just in-memory activation
    // state. Composes the Application layer exactly like CaseCqrsIntegrationTests does (register
    // the fixture's co-hosted Orleans client, let AddWayfinderApplication/AddWayfinderIdentity wire the
    // resolver/seeder) - nothing here touches CaseRequestContext or the existing Unit-1 handlers,
    // this suite is purely additive.
    [Collection(ClusterCollection.Name)]
    public class TenantResolverIntegrationTests
    {
        private readonly IClusterClient _clusterClient;
        private readonly IServiceProvider _rootProvider;

        public TenantResolverIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            _rootProvider = new ServiceCollection()
                .AddSingleton(_clusterClient)
                .AddWayfinderApplication()
                .BuildServiceProvider();
        }

        [Fact]
        public async Task Resolve__Given_TwoSeededTenants__Then_EachSubjectResolvesToItsOwnTenantAndRoles()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var userA = Guid.NewGuid();
            var userB = Guid.NewGuid();
            var subjectA = $"sub-a-{Guid.NewGuid()}";
            var subjectB = $"sub-b-{Guid.NewGuid()}";

            var seeder = _rootProvider.GetRequiredService<IIdentityRegistrySeeder>();

            await seeder.SeedAsync(new TenantSeed
            {
                TenantId = tenantA,
                Name = "Tenant A",
                Oidc = new TenantOidcConfig
                {
                    Issuer = "https://tenant-a.example/oidc",
                    Audience = "case-flow",
                    MetadataAddress = "https://tenant-a.example/.well-known/openid-configuration"
                },
                Users = new[]
                {
                    new UserSeed { Subject = subjectA, UserId = userA, Roles = new[] { "Admin", "Reviewer" } }
                }
            });

            await seeder.SeedAsync(new TenantSeed
            {
                TenantId = tenantB,
                Name = "Tenant B",
                Oidc = new TenantOidcConfig
                {
                    Issuer = "https://tenant-b.example/oidc",
                    Audience = "case-flow"
                },
                Users = new[]
                {
                    new UserSeed { Subject = subjectB, UserId = userB, Roles = new[] { "Member" } }
                }
            });

            var resolver = _rootProvider.GetRequiredService<ITenantResolver>();

            var resolvedA = await resolver.ResolveAsync(subjectA);
            resolvedA.Resolved.Should().BeTrue();
            resolvedA.TenantId.Should().Be(tenantA);
            resolvedA.UserId.Should().Be(userA);
            resolvedA.Roles.Should().BeEquivalentTo(new[] { "Admin", "Reviewer" });

            var resolvedB = await resolver.ResolveAsync(subjectB);
            resolvedB.Resolved.Should().BeTrue();
            resolvedB.TenantId.Should().Be(tenantB);
            resolvedB.UserId.Should().Be(userB);
            resolvedB.Roles.Should().BeEquivalentTo(new[] { "Member" });

            // Isolation: tenant A's subject must never resolve into tenant B's tenant id or vice
            // versa - the whole point of a per-subject registry.
            resolvedA.TenantId.Should().NotBe(tenantB);
            resolvedB.TenantId.Should().NotBe(tenantA);
        }

        [Fact]
        public async Task Resolve__Given_UnknownSubject__Then_NotResolvedAndDoesNotThrow()
        {
            var resolver = _rootProvider.GetRequiredService<ITenantResolver>();

            var resolved = await resolver.ResolveAsync($"never-registered-{Guid.NewGuid()}");

            resolved.Resolved.Should().BeFalse();
            resolved.TenantId.Should().Be(Guid.Empty);
            resolved.UserId.Should().Be(Guid.Empty);
            resolved.Roles.Should().BeEmpty();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Resolve__Given_BlankSubject__Then_NotResolvedAndDoesNotThrow(string blankSubject)
        {
            var resolver = _rootProvider.GetRequiredService<ITenantResolver>();

            var resolved = await resolver.ResolveAsync(blankSubject);

            resolved.Resolved.Should().BeFalse();
        }

        [Fact]
        public async Task UserIdentityGrain__Given_RegisteredThenDeactivated__Then_StateRehydratesFromStorage()
        {
            var subject = $"rehydrate-user-{Guid.NewGuid()}";
            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            var grain = _clusterClient.GetGrain<IUserIdentityGrain>(subject);
            await grain.Register(new UserIdentityRecord
            {
                TenantId = tenantId,
                UserId = userId,
                Roles = new[] { "Owner" }
            });

            await ForceDeactivateAllActivations();

            // A fresh grain reference to the SAME key forces Orleans to reactivate - if state were
            // only held in-memory (not actually written through IPersistentState), this would come
            // back unregistered instead of round-tripping the seeded values.
            var reactivated = _clusterClient.GetGrain<IUserIdentityGrain>(subject);
            var resolved = await reactivated.TryResolve();

            resolved.Should().NotBeNull();
            resolved.TenantId.Should().Be(tenantId);
            resolved.UserId.Should().Be(userId);
            resolved.Roles.Should().BeEquivalentTo(new[] { "Owner" });
        }

        [Fact]
        public async Task TenantGrain__Given_SeededThenDeactivated__Then_StateRehydratesFromStorage()
        {
            var tenantId = Guid.NewGuid();
            var oidc = new TenantOidcConfig
            {
                Issuer = "https://rehydrate.example/oidc",
                Audience = "case-flow",
                MetadataAddress = "https://rehydrate.example/.well-known/openid-configuration"
            };

            var grain = _clusterClient.GetGrain<ITenantGrain>(tenantId);
            await grain.Seed("Rehydrate Tenant", oidc);

            await ForceDeactivateAllActivations();

            var reactivated = _clusterClient.GetGrain<ITenantGrain>(tenantId);
            var record = await reactivated.Get();

            record.Should().NotBeNull();
            record.TenantId.Should().Be(tenantId);
            record.Name.Should().Be("Rehydrate Tenant");
            record.Oidc.Issuer.Should().Be(oidc.Issuer);
            record.Oidc.Audience.Should().Be(oidc.Audience);
            record.Oidc.MetadataAddress.Should().Be(oidc.MetadataAddress);
        }

        [Fact]
        public async Task UserIdentityGrain__Given_NeverRegistered__Then_TryResolveReturnsNullNotThrow()
        {
            var grain = _clusterClient.GetGrain<IUserIdentityGrain>($"never-{Guid.NewGuid()}");

            var resolved = await grain.TryResolve();

            resolved.Should().BeNull();
        }

        [Fact]
        public async Task TenantGrain__Given_NeverSeeded__Then_GetReturnsNullNotThrow()
        {
            var grain = _clusterClient.GetGrain<ITenantGrain>(Guid.NewGuid());

            var record = await grain.Get();

            record.Should().BeNull();
        }

        // Orleans's standard test-time mechanism for proving [PersistentState] round-trips through
        // real storage rather than surviving only in an activation's in-memory State object: force
        // the activation collector to run with a zero idle-age limit, which deactivates every
        // currently-idle activation in the cluster. The next call against the same grain key must
        // then reactivate and re-read from the backing IGrainStorage
        // (AddMemoryGrainStorageAsDefault in ClusterFixture's TestSiloConfigurator - see
        // UserIdentityGrain's remarks for why "Default" is the correct storage name in both the
        // test cluster and Wayfinder.Silo/Program.cs).
        private async Task ForceDeactivateAllActivations()
        {
            var managementGrain = _clusterClient.GetGrain<IManagementGrain>(0);
            await managementGrain.ForceActivationCollection(TimeSpan.Zero);
        }
    }
}
