using System;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.PlanningTable;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Orleans;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Plan.PlanItem.PlanningTable
{
    [Collection(ClusterCollection.Name)]
    public class PlanningTableGrainTests
    {
        private readonly IClusterClient _clusterClient;

        public PlanningTableGrainTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        [Theory, AutoData]
        public async Task GetPlannableItems__Given_Defined__When_NoApplicabilityRule__Then_GetItem
            (string caseDefinitionId, Guid caseInstanceId)
        {
            var expectedResult = new DiscretionaryItem();
            var definition = new Interfaces.Model.PlanningTable
            {
                TableItems =
                {
                    expectedResult
                }
            };

            var subject = _clusterClient.GetGrain<IPlanningTableGrain>(caseInstanceId, ShortGuid.NewGuid());

            await subject.Define(caseDefinitionId, definition);

            var result = await subject.GetPlannableItems();

            result.Should()
                .ContainSingle()
                .And.Contain(expectedResult);
        }

        [Theory, AutoData]
        public async Task GetPlannableItems__Given_Defined__When_ApplicabilityRuleTrue__Then_ReturnItem
            (string caseDefinitionId, Guid caseInstanceId)
        {
            var expectedResult = new DiscretionaryItem
            {
                ApplicabilityRuleRefs = new[]
                {
                    Rules.IsApplicable.Id
                }
            };

            var definition = new Interfaces.Model.PlanningTable
            {
                ApplicabilityRules =
                {
                    Rules.IsApplicable
                },
                TableItems =
                {
                    expectedResult
                }
            };

            var subject = _clusterClient.GetGrain<IPlanningTableGrain>(caseInstanceId, ShortGuid.NewGuid());

            await subject.Define(caseDefinitionId, definition);

            var result = await subject.GetPlannableItems();

            result.Should()
                .ContainSingle()
                .And.Contain(expectedResult);
        }

        [Theory, AutoData]
        public async Task GetPlannableItems__Given_Defined__When_ApplicabilityRuleFalse__Then_ReturnNone
            (string caseDefinitionId, Guid caseInstanceId)
        {
            var definition = new Interfaces.Model.PlanningTable
            {
                ApplicabilityRules =
                {
                    Rules.NotApplicable
                },
                TableItems =
                {
                    new DiscretionaryItem
                    {
                        ApplicabilityRuleRefs = new[]
                        {
                            Rules.NotApplicable.Id
                        }
                    }
                }
            };

            var subject = _clusterClient.GetGrain<IPlanningTableGrain>(caseInstanceId, ShortGuid.NewGuid());

            await subject.Define(caseDefinitionId, definition);

            var result = await subject.GetPlannableItems();

            result.Should()
                .BeEmpty();
        }

        [Theory, AutoData]
        public async Task GetPlannableItems__Given_Defined__When_NestedPlanningTable__Then_ReturnItem
            (string caseDefinitionId, Guid caseInstanceId)
        {
            var expectedResult = new DiscretionaryItem
            {
                ApplicabilityRuleRefs = new[]
                {
                    Rules.IsApplicable.Id
                }
            };
            var definition = new Interfaces.Model.PlanningTable
            {
                ApplicabilityRules =
                {
                    Rules.NotApplicable
                },
                TableItems =
                {
                    new Interfaces.Model.PlanningTable
                    {
                        ApplicabilityRules =
                        {
                            Rules.IsApplicable
                        },
                        TableItems =
                        {
                            expectedResult
                        }
                    }
                }
            };

            var subject = _clusterClient.GetGrain<IPlanningTableGrain>(caseInstanceId, ShortGuid.NewGuid());

            await subject.Define(caseDefinitionId, definition);

            var result = await subject.GetPlannableItems();

            result.Should()
                .ContainSingle()
                .And.Contain(expectedResult);
        }
    }
}
