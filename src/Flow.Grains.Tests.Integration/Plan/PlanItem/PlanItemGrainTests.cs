using System;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Flow.Grains.Interfaces;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Plan.Case;
using Flow.Grains.Plan.PlanItem;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Orleans;
using Xunit;

namespace Flow.Grains.Tests.Integration.Plan.PlanItem
{
    [Collection(ClusterCollection.Name)]
    public class PlanItemGrainTests
    {
        private IClusterClient ClusterClient { get; }

        public PlanItemGrainTests(ClusterFixture fixture)
        {
            ClusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        [Theory, AutoData]
        public async Task GetState__When_Undefined__Then_Uninitialized
            (Guid caseInstanceId)
        {
            var subject = ClusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{ShortGuid.NewGuid()}.{ShortGuid.NewGuid()}");

            var result = await subject.GetSnapshot();

            result.Should().NotBeNull();
            result.PlanItemState.Should().Be(PlanItemState.Uninitialized);
        }

        // TODO: these tests need to define the plan item first in order for StateMachine to be initialized
        [Theory, AutoData]
        public async Task Trigger__When_Undefined__Then_InvalidOperationEx
            (Guid caseInstanceId)
        {
            var subject = ClusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{ShortGuid.NewGuid()}.{ShortGuid.NewGuid()}");

            await subject
                .Awaiting(x => x.Trigger(PlanItemTransition.Create))
                .Should()
                .ThrowAsync<InvalidOperationException>();
        }

        [Theory, AutoData]
        public async Task Define__Given_PlanItem__When_CaseUndefined__Then_ThrowInvalidOperationException
            (string caseDefinitionId, Guid caseInstanceId)
        {
            var subject = ClusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{ShortGuid.NewGuid()}.{ShortGuid.NewGuid()}");

            await subject
                .Awaiting(x => x.Define(caseDefinitionId, new Interfaces.Model.PlanItem {DefinitionRef = "not_defined"}))
                .Should()
                .ThrowAsync<InvalidOperationException>();
        }

        [Theory, AutoData]
        public async Task Define__Given_PlanItem__When_CaseDefined__Then_TBD
            (Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                DefinitionRef = "MilestoneA"
            };

            var @case = new Case
            {
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
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, @case.Id)
                .Define(@case);

            var subject = ClusterClient
                .GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{@case.CasePlanModel.Id}.{planItem.Id}");

            await subject
                .Awaiting(x => x.Define(@case.Id, planItem))
                .Should()
                .NotThrowAsync();
        }

        [Theory, AutoData]
        public async Task Trigger__Given_Create__When_Defined__Then_Available
            (Guid caseInstanceId)
        {
            var planItem = new Interfaces.Model.PlanItem
            {
                Id = "PlanItemA",
                DefinitionRef = "MilestoneA"
            };

            var @case = new Case
            {
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
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, @case.Id)
                .Define(@case);

            var subject = ClusterClient
                .GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{@case.CasePlanModel.Id}.{planItem.Id}");

            await subject
                .Awaiting(x => x.Define(@case.Id, planItem))
                .Should()
                .NotThrowAsync();

            await subject.Trigger(PlanItemTransition.Create);

            var result = await subject.GetSnapshot();

            result.Should().NotBeNull();
            result.PlanItemState.Should().Be(PlanItemState.Available);
        }
    }
}
