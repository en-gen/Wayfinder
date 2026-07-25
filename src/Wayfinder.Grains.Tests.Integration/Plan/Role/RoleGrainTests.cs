using System;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Interfaces;
using Flow.Grains.Plan.Role;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Orleans;
using Xunit;

namespace Flow.Grains.Tests.Integration.Plan.Role
{
    [Collection(ClusterCollection.Name)]
    public class RoleGrainTests
    {
        private readonly IClusterClient _clusterClient;

        public RoleGrainTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        [Theory, AutoData]
        public async Task Authorize__Given_Undefined__When_RoleInContext__Then_False
            (Guid caseInstanceId)
        {
            var roleGrain = _clusterClient.GetGrain<IRoleGrain>(caseInstanceId, ShortGuid.NewGuid());

            CaseRequestContext.UserRoles = new[]
            {
                "super",
                "admin",
                "user"
            };

            var result = await roleGrain.Authorize(new GrainCancellationTokenSource().Token);

            result.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task Authorize__Given_Undefined__When_RoleNotInContext__Then_False
            (Guid caseInstanceId)
        {
            var roleGrain = _clusterClient.GetGrain<IRoleGrain>(caseInstanceId, ShortGuid.NewGuid());

            CaseRequestContext.UserRoles = new[]
            {
                "super",
                "user"
            };

            var result = await roleGrain.Authorize(new GrainCancellationTokenSource().Token);

            result.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task Authorize__Given_Defined__When_RoleInContext__Then_True
            (string caseDefinitionId, Guid caseInstanceId)
        {
            var role = new Interfaces.Model.Role
            {
                Name = "admin"
            };

            var roleGrain = _clusterClient.GetGrain<IRoleGrain>(caseInstanceId, role.Id);

            await roleGrain.Define(caseDefinitionId, role);

            CaseRequestContext.UserRoles = new[]
            {
                "super",
                "admin",
                "user"
            };

            var result = await roleGrain.Authorize(new GrainCancellationTokenSource().Token);

            result.Should().BeTrue();
        }

        [Theory, AutoData]
        public async Task Authorize__Given_Defined__When_RoleNotInContext__Then_False
            (string caseDefinitionId, Guid caseInstanceId)
        {
            var role = new Interfaces.Model.Role
            {
                Name = "admin"
            };

            var roleGrain = _clusterClient.GetGrain<IRoleGrain>(caseInstanceId, role.Id);

            await roleGrain.Define(caseDefinitionId, role);

            CaseRequestContext.UserRoles = new[]
            {
                "super",
                "user"
            };

            var result = await roleGrain.Authorize(new GrainCancellationTokenSource().Token);

            result.Should().BeFalse();
        }
    }
}
