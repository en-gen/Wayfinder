using System.Threading.Tasks;
using Flow.Grains.Interfaces.Model;
using Flow.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Xunit;

namespace Flow.Grains.Tests.Integration.Conformance
{
    // ADO #21 - conformance scenarios for model-driven instantiation (§8.7 planning + Table 8.6
    // create) and the discretionary-item exclusion (5.4.9.2/8.7). See Conformance/COVERAGE.md.
    [Collection(ClusterCollection.Name)]
    public class InstantiationScenarios
    {
        private readonly ConformanceHarness _harness;

        public InstantiationScenarios(ClusterFixture fixture)
        {
            _harness = new ConformanceHarness(fixture.ClusterClient);
        }

        // 8.7: "If a Stage instance is in Active state, then the planned PlanItems are
        // instantiated immediately" - the CasePlanModel's create-entry to Active (Table 8.6) must
        // instantiate EVERY PlanItem of the plan: the Milestone lands in Available (Table 8.11
        // create) and the HumanTask in Enabled (Table 8.8 create + enable via Table 5.51's
        // default-TRUE ManualActivationRule).
        [Fact]
        [ConformanceCitation("8.7 / planned PlanItems instantiate on Active")]
        [ConformanceCitation("Table 8.11 / create")]
        [ConformanceCitation("Table 8.8 / create + enable")]
        public async Task Instantiation__Given_MultiplePlanItems__Then_AllInstantiateToTheirTableMandatedStates()
        {
            var deployed = await _harness.DeployAndCreate("Instantiation_MultiplePlanItems.cmmn");

            var milestoneGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemMilestone", deployed.Scope);
            var taskGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemTask", deployed.Scope);

            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "Table 8.11 (create): a Milestone instance is created into Available - and it must have been created at all (8.7)");

            (await taskGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Enabled,
                "Table 8.8 (create): the Task is created into Available, and with no entry criteria and Table 5.51's default-TRUE ManualActivationRule it moves to Enabled - observable only if it was instantiated at all (8.7)");

            deployed.AfterCreateSnapshot.BehaviorExtension.Children.Should().HaveCount(2,
                "8.7: every PlanItem of the plan - exactly the two declared - must have been instantiated");
        }

        // 8.7 Planning: "Users (Case workers) are said to 'plan' (in run-time), when they select
        // DiscretionaryItems from a PlanningTable" - a DiscretionaryItem is planned at run-time
        // by a Case worker, never auto-instantiated with the fixed plan (5.4.9.2).
        [Fact]
        [ConformanceCitation("5.4.9.2, 8.7 / DiscretionaryItem excluded from auto-instantiation")]
        public async Task Instantiation__Given_PlanningTableDiscretionaryItem__Then_NotAutoInstantiated()
        {
            var deployed = await _harness.DeployAndCreate("Discretionary_ItemExcluded.cmmn");

            var milestoneGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);
            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "the fixed PlanItem must have been instantiated");

            deployed.AfterCreateSnapshot.BehaviorExtension.Children.Should().NotContainKey("DiscretionaryTaskA",
                "5.4.9.2/8.7: DiscretionaryItems are planned at a Case worker's run-time discretion, never auto-instantiated");
            deployed.AfterCreateSnapshot.BehaviorExtension.Children.Should().HaveCount(1,
                "only the one fixed PlanItem may exist - the DiscretionaryItem must not have added a second child");
        }
    }
}
