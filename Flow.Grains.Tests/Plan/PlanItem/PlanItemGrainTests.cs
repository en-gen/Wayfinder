using System;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Plan.PlanItem.Definitions;
using Flow.Grains.Tests.SiloFixture;
using FluentAssertions;
using Orleans;
using Orleans.Hosting;
using Xunit;

namespace Flow.Grains.Tests.Plan.PlanItem
{
    [Collection(ClusterCollection.Name)]
    public class PlanItemGrainTests
    {
        private ISiloHost SiloHost { get; }
        private IClusterClient ClusterClient { get; }

        public PlanItemGrainTests(ClusterFixture fixture)
        {
            SiloHost = fixture.SiloHost;
            ClusterClient = fixture.ClusterClient;
        }

        [Theory, AutoData]
        public async Task GetState__When_Undefined__Then_Uninitialized
            (Guid caseInstanceId)
        {
            var subject = ClusterClient.GetGrain<IPlanItemGrain>(caseInstanceId, $"{ShortGuid.NewGuid()}.{ShortGuid.NewGuid()}");

            var result = await subject.GetSnapshot();

            result.Should().NotBeNull();
            result.PlanItemState.Should().BeEquivalentTo(PlanItemState.Uninitialized);
        }

        // TODO: these tests need to define the plan item first in order for StateMachine to be initialized
        [Theory, AutoData]
        public async Task Trigger__When_Undefined__Then_InvalidOperationEx
            (Guid caseInstanceId)
        {
            var subject = ClusterClient.GetGrain<IPlanItemGrain>(caseInstanceId, $"{ShortGuid.NewGuid()}.{ShortGuid.NewGuid()}");

            await subject
                .Awaiting(x => x.Trigger(PlanItemTransition.Create))
                .Should()
                .ThrowAsync<InvalidOperationException>();
        }

        [Theory, AutoData]
        public async Task Define__Given_PlanItem__When_CaseUndefined__Then_ThrowInvalidOperationException
            (Guid caseInstanceId, Guid caseDefinitionId)
        {
            var subject = ClusterClient.GetGrain<IPlanItemGrain>(caseInstanceId, $"{ShortGuid.NewGuid()}.{ShortGuid.NewGuid()}");

            await subject
                .Awaiting(x => x.Define(caseDefinitionId, new Interfaces.Model.PlanItem {DefinitionRef = "not_defined"}))
                .Should()
                .ThrowAsync<InvalidOperationException>();
        }

        [Theory, AutoData]
        public async Task Define__Given_PlanItem__When_CaseDefined__Then_TBD
            (Guid caseInstanceId, Guid caseDefinitionId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                DefinitionRef = "MilestoneA"
            };

            var @case = new Case
            {
                Id = caseDefinitionId.ToString(),
                CasePlanModel = new Stage
                {
                    Id = "CPM",
                    PlanItemDefinitions =
                    {
                        new Milestone {Id = "MilestoneA"}
                    },
                    PlanItems =
                    {
                        planItem
                    }
                }
            };

            await ClusterClient
                .GetGrain<IPlanItemDefinitionGraphGrain>(caseDefinitionId)
                .Construct(@case);

            var subject = ClusterClient
                .GetGrain<IPlanItemGrain>(caseInstanceId, $"{@case.CasePlanModel.Id}.{planItem.Id}");

            await subject
                .Awaiting(x => x.Define(caseDefinitionId, planItem))
                .Should()
                .NotThrowAsync();
        }

        [Theory, AutoData]
        public async Task Trigger__Given_Create__When_Defined__Then_Available
            (Guid caseInstanceId, Guid caseDefinitionId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                DefinitionRef = "MilestoneA"
            };

            var @case = new Case
            {
                Id = caseDefinitionId.ToString(),
                CasePlanModel = new Stage
                {
                    Id = "CPM",
                    PlanItemDefinitions =
                    {
                        new Milestone {Id = "MilestoneA"}
                    },
                    PlanItems =
                    {
                        planItem
                    }
                }
            };

            await ClusterClient
                .GetGrain<IPlanItemDefinitionGraphGrain>(caseDefinitionId)
                .Construct(@case);

            var subject = ClusterClient
                .GetGrain<IPlanItemGrain>(caseInstanceId, $"{@case.CasePlanModel.Id}.{planItem.Id}");

            await subject
                .Awaiting(x => x.Define(caseDefinitionId, planItem))
                .Should()
                .NotThrowAsync();

            await subject.Trigger(PlanItemTransition.Create);

            var result = await subject.GetSnapshot();

            result.Should().NotBeNull();
            result.PlanItemState.Should().BeEquivalentTo(PlanItemState.Available);
        }
    }
}
