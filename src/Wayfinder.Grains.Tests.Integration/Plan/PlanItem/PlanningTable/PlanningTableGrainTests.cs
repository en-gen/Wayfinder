using System;
using System.Linq;
using System.Threading.Tasks;
using AutoFixture.Xunit2;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Plan.CmmnElement.Events;
using Wayfinder.Grains.Plan.PlanningTable;
using Wayfinder.Grains.Plan.PlanningTable.Events;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using Wayfinder.Grains.Tests.Utils.Helpers;
using FluentAssertions;
using Orleans;
using Orleans.Runtime;
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

        // #158 companion (identical-twin defect in PlanningTableGrain.EvaluateApplicabilityRule):
        // ApplicabilityRule's spec default is TRUE (5.36), and Rules.ErroringApplicabilityRule's
        // condition genuinely throws in the real Jint engine here (integration, not mocked) rather
        // than evaluating to false - on the unfixed `?.Value ?? true` this passed anyway by
        // coincidence for the SAME defaultResult=true reason ManualActivationRule's did NOT: here
        // the bug's fail-CLOSED direction (default(bool) == false) actively fights a TRUE default,
        // so an erroring rule wrongly excluded the item. This pins that ValueOr(true) now wins.
        [Theory, AutoData]
        public async Task GetPlannableItems__Given_Defined__When_ApplicabilityRuleErrors__Then_ReturnItem
            (string caseDefinitionId, Guid caseInstanceId)
        {
            var expectedResult = new DiscretionaryItem
            {
                ApplicabilityRuleRefs = new[]
                {
                    Rules.ErroringApplicabilityRule.Id
                }
            };

            var definition = new Interfaces.Model.PlanningTable
            {
                ApplicabilityRules =
                {
                    Rules.ErroringApplicabilityRule
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
                .ContainSingle("an erroring ApplicabilityRule must fall back to its spec default of TRUE, not be silently excluded")
                .And.Contain(expectedResult);
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

        // #184 - GetPlannableItems is a read-only query. It must not permanently mutate this
        // grain's journal: repeated polling of an unchanged planning table should not accumulate
        // journal entries 1:1 with read volume. ICmmnElementGrain.GetJournaledEvents() reads the
        // grain's own confirmed journal directly (CmmnElementGrain.cs), and
        // IManagementGrain.ForceActivationCollection forces a real activation recycle, proving
        // whatever is asserted here actually ended up (or didn't) permanently in the journal -
        // not just visible from within the activation that ran the reads.
        [Theory, AutoData]
        public async Task GetPlannableItems__Given_RepeatedReadOnlyPolling__Then_JournalDoesNotGrowWithReadVolume
            (string caseDefinitionId, Guid caseInstanceId)
        {
            var discretionaryItem = new DiscretionaryItem
            {
                ApplicabilityRuleRefs = new[] { Rules.IsApplicable.Id }
            };

            var definition = new Interfaces.Model.PlanningTable
            {
                ApplicabilityRules = { Rules.IsApplicable },
                TableItems = { discretionaryItem }
            };

            var subject = _clusterClient.GetGrain<IPlanningTableGrain>(caseInstanceId, ShortGuid.NewGuid());

            await subject.Define(caseDefinitionId, definition);

            const int pollCount = 20;
            for (var i = 0; i < pollCount; i++)
            {
                var plannable = await subject.GetPlannableItems();

                plannable.Should().ContainSingle().And.Contain(discretionaryItem,
                    $"GetPlannableItems must keep answering correctly on read #{i + 1} - this is a journaling-hygiene fix, not a change to query behavior");
            }

            // force this activation to recycle so the assertion below reflects the PERMANENT
            // journal, not merely what is visible from within the activation that did the reads
            await _clusterClient.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

            var journalAfterReads = await subject.GetJournaledEvents();

            journalAfterReads.OfType<ApplicabilityRuleEvaluated>().Should().BeEmpty(
                "GetPlannableItems is read-only and must not journal an ApplicabilityRuleEvaluated event per rule per call (#184)");

            journalAfterReads.OfType<CmmnElementDefined<Interfaces.Model.PlanningTable>>().Should().HaveCount(1,
                "Define is a genuine, deliberately-confirmed write and is unaffected by this fix");
        }
    }
}
