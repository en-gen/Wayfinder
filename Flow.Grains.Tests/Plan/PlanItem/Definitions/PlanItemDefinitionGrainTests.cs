using System;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem.Definitions;
using Flow.Grains.Tests.SiloFixture;
using FluentAssertions;
using Orleans;
using Orleans.Hosting;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem.Definitions
{
    [Collection(ClusterCollection.Name)]
    public class PlanItemDefinitionGrainTests
    {
        private ISiloHost SiloHost { get; }
        private IClusterClient ClusterClient { get; }

        public PlanItemDefinitionGrainTests(ClusterFixture fixture)
        {
            SiloHost = fixture.SiloHost;
            ClusterClient = fixture.ClusterClient;
        }

        [Theory, AutoData]
        public async Task IsDefined__Given_DefinitionId__When_Undefined_Then_ReturnFalse
            (Guid caseId)
        {
            var cpmId = ShortGuid.NewGuid();
            var defId = ShortGuid.NewGuid();

            var defGrain = ClusterClient.GetGrain<IPlanItemDefinitionGrain>(caseId, $"{cpmId}.{defId}");

            var isDefined = await defGrain.IsDefined();

            isDefined.Should().BeFalse();
        }

        [Theory, AutoData]
        public async Task Define__Given_PlanItemDefinition__When_UndefinedAndIdsMatch__Then_StoreDefinition
            (Guid caseId)
        {
            var cpmId = ShortGuid.NewGuid();
            var defId = ShortGuid.NewGuid();

            var defGrain = ClusterClient.GetGrain<IPlanItemDefinitionGrain>(caseId, $"{cpmId}.{defId}");

            await defGrain
                .Awaiting(x => x.Define(new PlanItemDefinition {Id = defId}))
                .Should()
                .NotThrowAsync();
        }

        [Theory, AutoData]
        public async Task Define__Given_PlanItemDefinition__When_UndefinedAndIdsNotMatch__Then_ThrowArgumentException
            (Guid caseId)
        {
            var cpmId = ShortGuid.NewGuid();
            var defId = ShortGuid.NewGuid();

            var defGrain = ClusterClient.GetGrain<IPlanItemDefinitionGrain>(caseId, $"{cpmId}.{defId}");

            await defGrain
                .Awaiting(x => x.Define(new PlanItemDefinition {Id = Guid.NewGuid().ToString()}))
                .Should()
                .ThrowAsync<ArgumentException>();
        }

        [Theory, AutoData]
        public async Task Define__Given_PlanItemDefinition__When_PreviouslyDefined__Then_ThrowInvalidOperationException
            (Guid caseId)
        {
            var cpmId = ShortGuid.NewGuid();
            var defId = ShortGuid.NewGuid();

            var defGrain = ClusterClient.GetGrain<IPlanItemDefinitionGrain>(caseId, $"{cpmId}.{defId}");

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

            var defGrain = ClusterClient.GetGrain<IPlanItemDefinitionGrain>(caseId, $"{cpmId}.{defId}");

            await defGrain.Define(new PlanItemDefinition {Id = defId});

            var isDefined = await defGrain.IsDefined();

            isDefined.Should().BeTrue();
        }
    }
}
