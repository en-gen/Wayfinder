using System;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanItem.Definitions;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Orleans;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Plan.PlanItem.Definitions
{
    [Collection(ClusterCollection.Name)]
    public class PlanItemDefinitionGrainTests
    {
        private readonly IClusterClient _clusterClient;

        public PlanItemDefinitionGrainTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;
        }

        [Theory, AutoData]
        public async Task IsDefined__Given_DefinitionId__When_Undefined_Then_ReturnFalse
            (Guid caseId)
        {
            var cpmId = ShortGuid.NewGuid();
            var defId = ShortGuid.NewGuid();

            var defGrain = _clusterClient.GetGrain<IPlanItemDefinitionGrain>(caseId, $"{cpmId}.{defId}");

            var isDefined = await defGrain.IsDefined();

            isDefined.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task Define__Given_PlanItemDefinition__When_UndefinedAndIdsMatch__Then_StoreDefinition
            (Guid caseId)
        {
            var cpmId = ShortGuid.NewGuid();
            var defId = ShortGuid.NewGuid();

            var defGrain = _clusterClient.GetGrain<IPlanItemDefinitionGrain>(caseId, $"{cpmId}.{defId}");

            await defGrain
                .Awaiting(x => x.Define(new PlanItemDefinition { Id = defId }))
                .Should()
                .NotThrowAsync();
        }

        [Theory, AutoData]
        public async Task Define__Given_PlanItemDefinition__When_UndefinedAndIdsNotMatch__Then_ThrowArgumentException
            (Guid caseId)
        {
            var cpmId = ShortGuid.NewGuid();
            var defId = ShortGuid.NewGuid();

            var defGrain = _clusterClient.GetGrain<IPlanItemDefinitionGrain>(caseId, $"{cpmId}.{defId}");

            await defGrain
                .Awaiting(x => x.Define(new PlanItemDefinition { Id = Guid.NewGuid().ToString() }))
                .Should()
                .ThrowAsync<ArgumentException>();
        }

        [Theory, AutoData]
        public async Task Define__Given_PlanItemDefinition__When_PreviouslyDefined__Then_ThrowInvalidOperationException
            (Guid caseId)
        {
            var cpmId = ShortGuid.NewGuid();
            var defId = ShortGuid.NewGuid();

            var defGrain = _clusterClient.GetGrain<IPlanItemDefinitionGrain>(caseId, $"{cpmId}.{defId}");

            await defGrain
                .Awaiting(x => x.Define(new PlanItemDefinition { Id = defId }))
                .Should()
                .NotThrowAsync();

            await defGrain
                .Awaiting(x => x.Define(new PlanItemDefinition()))
                .Should()
                .ThrowAsync<InvalidOperationException>();

        }

        [Theory, AutoData]
        public async Task IsDefined__Given_DefinitionId__When_Defined__Then_ReturnTrue
            (Guid caseId)
        {
            var cpmId = ShortGuid.NewGuid();
            var defId = ShortGuid.NewGuid();

            var defGrain = _clusterClient.GetGrain<IPlanItemDefinitionGrain>(caseId, $"{cpmId}.{defId}");

            await defGrain.Define(new PlanItemDefinition { Id = defId });

            var isDefined = await defGrain.IsDefined();

            isDefined.Should().BeTrue();
        }
    }
}
