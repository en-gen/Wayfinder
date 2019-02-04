using System;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Plan.Role;
using Flow.Grains.Plan.Role;
using Flow.Grains.Tests.SiloFixture;
using FluentAssertions;
using Orleans;
using Orleans.Hosting;
using Xunit;

namespace Flow.Grains.Tests.Plan.Role
{
    [Collection(ClusterCollection.Name)]
    public class RoleGrainTests
    {
        private ISiloHost SiloHost { get; }
        private IClusterClient ClusterClient { get; }

        public RoleGrainTests(ClusterFixture fixture)
        {
            SiloHost = fixture.SiloHost;
            ClusterClient = fixture.ClusterClient;
        }

        [Theory, AutoData]
        public async Task Authorize__Given_Undefined__When_RoleInContext__Then_False
            (Guid caseInstanceId)
        {
            var roleGrain = ClusterClient.GetGrain<IRoleGrain>(caseInstanceId, ShortGuid.NewGuid());

            RoleRegistrar.Register(new[]
            {
                "super",
                "admin",
                "user"
            });

            var result = await roleGrain.Authorize(new GrainCancellationTokenSource().Token);

            result.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task Authorize__Given_Undefined__When_RoleNotInContext__Then_False
            (Guid caseInstanceId)
        {
            var roleGrain = ClusterClient.GetGrain<IRoleGrain>(caseInstanceId, ShortGuid.NewGuid());

            RoleRegistrar.Register(new[]
            {
                "super",
                "user"
            });

            var result = await roleGrain.Authorize(new GrainCancellationTokenSource().Token);

            result.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task Authorize__Given_Defined__When_RoleInContext__Then_True
            (Guid caseDefinitionId, Guid caseInstanceId)
        {
            var role = new Interfaces.Model.Role
            {
                Name = "admin"
            };
            
            var roleGrain = ClusterClient.GetGrain<IRoleGrain>(caseInstanceId, role.Id);

            await roleGrain.Define(caseDefinitionId, role);
            
            RoleRegistrar.Register(new[]
            {
                "super",
                "admin",
                "user"
            });

            var result = await roleGrain.Authorize(new GrainCancellationTokenSource().Token);

            result.Should().BeTrue();
        }

        [Theory, AutoData]
        public async Task Authorize__Given_Defined__When_RoleNotInContext__Then_False
            (Guid caseDefinitionId, Guid caseInstanceId)
        {
            var role = new Interfaces.Model.Role
            {
                Name = "admin"
            };

            var roleGrain = ClusterClient.GetGrain<IRoleGrain>(caseInstanceId, role.Id);

            await roleGrain.Define(caseDefinitionId, role);

            RoleRegistrar.Register(new[]
            {
                "super",
                "user"
            });

            var result = await roleGrain.Authorize(new GrainCancellationTokenSource().Token);

            result.Should().BeFalse();
        }
    }
}
