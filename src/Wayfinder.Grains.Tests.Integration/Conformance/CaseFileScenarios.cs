using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.CaseFileItem;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Conformance
{
    // ADO #21 - conformance scenarios for §8.3's CaseFileItem lifecycle (Tables 8.1/8.2): the
    // operations are distinct standardEvents that drive sentries distinctly, and delete is
    // terminal. The create/update operations themselves are pinned throughout SentryScenarios;
    // this class covers the operations beyond that pair. See Conformance/COVERAGE.md.
    [Collection(ClusterCollection.Name)]
    public class CaseFileScenarios
    {
        private readonly ConformanceHarness _harness;

        public CaseFileScenarios(ClusterFixture fixture)
        {
            _harness = new ConformanceHarness(fixture.ClusterClient);
        }

        // Table 8.1/8.2 (replace): "Available -> Available" with its own standardEvent, distinct
        // from update - a replace-keyed OnPart must NOT fire on update (nor on create), and MUST
        // fire on replace.
        [Fact]
        [ConformanceCitation("Table 8.2 / replace")]
        [ConformanceCitation("Table 8.1 / replace vs update distinction")]
        public async Task CaseFile__Given_ReplaceKeyedOnPart__Then_OnlyReplaceSatisfiesIt()
        {
            var deployed = await _harness.DeployAndCreate("CaseFile_ReplaceDrivesOnPart.cmmn");

            var milestoneGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);

            var item = _harness.CaseFileItem(deployed.CaseInstanceId, "TheItem");
            await item.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "TheItem" }, JsonNode.Parse("""{"v": 1}"""));

            // update is a DIFFERENT standardEvent (Table 8.1) - it must not satisfy the
            // replace-keyed OnPart.
            await item.Update(JsonNode.Parse("""{"v": 2}"""));

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "Table 8.1: update and replace are distinct CaseFileItem transitions - an update must not satisfy a replace-keyed OnPart");

            await item.Replace(JsonNode.Parse("""{"v": 3}"""));

            var completed = await ConformanceHarness.PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);
            completed.Should().BeTrue(
                "Table 8.2 (replace): the replace operation must publish its own standardEvent and satisfy the replace-keyed OnPart");
        }

        // Table 8.2 (addChild): "Available -> Available" - one of the eight CaseFileItem
        // operations, exercised here as a sentry trigger. This pins the OPERATION as a runtime
        // transition; the static containment hierarchy itself is a documented capability gap
        // (CmmnCapabilityLint rule 5) and out of scope.
        [Fact]
        [ConformanceCitation("Table 8.2 / addChild")]
        public async Task CaseFile__Given_AddChildKeyedOnPart__Then_AddChildSatisfiesIt()
        {
            var deployed = await _harness.DeployAndCreate("CaseFile_AddChildDrivesOnPart.cmmn");

            var milestoneGrain = _harness.ResolveChild(
                deployed.CaseInstanceId, deployed.AfterCreateSnapshot.BehaviorExtension, "PlanItemA", deployed.Scope);

            var parentItem = _harness.CaseFileItem(deployed.CaseInstanceId, "ParentItem");
            var childItem = _harness.CaseFileItem(deployed.CaseInstanceId, "ChildItem");
            await parentItem.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ParentItem" }, JsonNode.Parse("""{"kind": "parent"}"""));
            await childItem.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "ChildItem" }, JsonNode.Parse("""{"kind": "child"}"""));

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            (await milestoneGrain.GetSnapshot()).PlanItemState.Should().Be(PlanItemState.Available,
                "creating the two items must not satisfy an addChild-keyed OnPart (Table 8.1: distinct transitions)");

            await parentItem.AddChild("ChildItem");

            var completed = await ConformanceHarness.PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed);
            completed.Should().BeTrue(
                "Table 8.2 (addChild): the addChild operation must publish its own standardEvent and satisfy the addChild-keyed OnPart");
        }

        // Table 8.1/8.2 (delete): "Available -> Discarded", and Discarded is terminal: "A
        // CaseFileItem instance in this state is considered deleted and is not available to Case
        // workers or expressions" - no further operation may succeed.
        [Fact]
        [ConformanceCitation("Table 8.2 / delete")]
        [ConformanceCitation("Table 8.1 / Discarded is terminal")]
        public async Task CaseFile__Given_DeletedItem__Then_DiscardedAndRejectsFurtherOperations()
        {
            var deployed = await _harness.DeployAndCreate("CaseFile_DeleteTerminalState.cmmn");

            var item = _harness.CaseFileItem(deployed.CaseInstanceId, "TheItem");
            await item.Create(deployed.CaseDefinitionId, new CaseFileItem { Id = "TheItem" }, JsonNode.Parse("""{"v": 1}"""));

            (await item.GetSnapshot()).CaseFileItemState.Should().Be(CaseFileItemState.Available,
                "Table 8.2 (create): a created CaseFileItem instance starts Available");

            await item.Delete();

            (await item.GetSnapshot()).CaseFileItemState.Should().Be(CaseFileItemState.Discarded,
                "Table 8.2 (delete): Available -> Discarded");

            var furtherUpdate = async () => await item.Update(JsonNode.Parse("""{"v": 2}"""));
            await furtherUpdate.Should().ThrowAsync<InvalidOperationException>(
                "Table 8.1: Discarded is terminal - a deleted CaseFileItem accepts no further operations");
        }
    }
}
