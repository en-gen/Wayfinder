using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interchange;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Plan.PlanItem;
using Wayfinder.Grains.Tests.Integration.SiloFixture;
using FluentAssertions;
using Orleans;
using Xunit;

namespace Wayfinder.Grains.Tests.Integration.Interchange
{
    // ADO #20 - THE FLAGSHIP: a .cmmn XML FILE runs end-to-end.
    // ~~~~~
    // MilestoneSentryCase.cmmn models the exact shape CasePlanModelInstantiationIntegrationTests
    // builds by hand in C# (Milestone + Sentry with a CaseFileItemOnPart + IfPart, plus a
    // CaseFileItemDefinition/CaseFileItem) - proving the same public front door
    // (ICaseDefinitionGrain.Define -> ICaseGrain.Create -> Trigger(Create)) that test exercises
    // for a hand-built model works identically for a model that arrived as spec-conformant CMMN
    // 1.1 XML (spec §9.3) and was only ever touched by CmmnXmlSerializer.Import +
    // CaseInterchangeExtensions.ToDeployableCase.
    [Collection(ClusterCollection.Name)]
    public class CmmnImportDeployIntegrationTests
    {
        private readonly IClusterClient _clusterClient;

        public CmmnImportDeployIntegrationTests(ClusterFixture fixture)
        {
            _clusterClient = fixture.ClusterClient;

            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        [Fact]
        public async Task Import__Given_MilestoneSentryCmmnFile__Then_DeploysAndRunsToMilestoneCompletion()
        {
            var xml = ReadEmbeddedResource("MilestoneSentryCase.cmmn");

            // Import: the .cmmn FILE, not a hand-built C# model, is the source of truth from here on.
            var importResult = CmmnXmlSerializer.Import(xml);
            importResult.IsError.Should().BeFalse(importResult.Message);

            // Honesty gate: this specific file has nothing this engine can't run - the lint must
            // say so before deployment proceeds, exactly as a real caller would check.
            var lintReport = CmmnCapabilityLint.Lint(importResult.Value);
            lintReport.HasUnsupported.Should().BeFalse(
                "MilestoneSentryCase.cmmn only uses Milestone/Sentry/CaseFileItemOnPart/IfPart with a Jint " +
                "expression - every construct it uses has a real runtime behavior");

            // Deploy seam: adapt the imported Definitions into the Case object
            // ICaseDefinitionGrain.Define already accepts - the only new step versus how existing
            // tests deploy a hand-built model.
            var @case = importResult.Value.ToDeployableCase();
            @case.Id = $"case-{ShortGuid.NewGuid()}"; // unique per test run; the sample file's own id is fixed.

            var caseDefinitionId = @case.Id;
            var caseInstanceId = Guid.NewGuid();
            const string scope = "CPM";
            const string caseFileItemId = "TheCaseFileItem";

            // From here on, this is exactly CasePlanModelInstantiationIntegrationTests' flow -
            // the front door, unmodified - now driving a definition that arrived as XML.
            await _clusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, caseDefinitionId)
                .Define(@case);

            var caseGrain = _clusterClient.GetGrain<ICaseGrain>(caseInstanceId, scope);
            await caseGrain.Create(caseDefinitionId);
            var afterCreateSnapshot = await caseGrain.Trigger(PlanItemTransition.Create);

            afterCreateSnapshot.PlanItemState.Should().Be(PlanItemState.Active,
                "Table 8.6: the CasePlanModel's create transition goes straight to Active, skipping Available");

            var milestoneGrain = ResolveChildGrain(caseInstanceId, afterCreateSnapshot, "PlanItemA", scope);

            var beforeSnapshot = await milestoneGrain.GetSnapshot();
            beforeSnapshot.PlanItemState.Should().Be(PlanItemState.Available,
                "the Milestone must have been instantiated by Case creation and be waiting on its entry criterion");

            // Drive the CaseFileItem the imported Sentry's CaseFileItemOnPart is waiting for.
            var caseFileItemGrain = _clusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);
            await caseFileItemGrain.Create(caseDefinitionId, new CaseFileItem { Id = caseFileItemId }, JsonNode.Parse("""{"amount": 0}"""));

            // amount=50: OnPart(Update) occurs but the imported IfPart (value.amount > 100) is
            // false - must not satisfy the sentry.
            await caseFileItemGrain.Update(JsonNode.Parse("""{"amount": 50}"""));

            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var afterFirstUpdate = await milestoneGrain.GetSnapshot();
            afterFirstUpdate.PlanItemState.Should().Be(PlanItemState.Available,
                "the imported IfPart condition is false at amount=50, so the sentry must not fire yet");

            // amount=150: OnPart(Update) occurs again, IfPart now true - sentry fires, Milestone
            // completes. THE PROOF: a model that started life as a .cmmn XML file ran end-to-end.
            await caseFileItemGrain.Update(JsonNode.Parse("""{"amount": 150}"""));

            var completed = await PollUntil(
                async () => (await milestoneGrain.GetSnapshot()).PlanItemState == PlanItemState.Completed,
                TimeSpan.FromSeconds(10));

            completed.Should().BeTrue(
                "the second Update transition should satisfy the imported Sentry's OnPart and IfPart, completing " +
                "the Milestone - proving the .cmmn file ran end-to-end from CmmnXmlSerializer.Import() through " +
                "ICaseGrain.Create()");
        }

        [Fact]
        public void Lint__Given_MilestoneSentryCmmnFile__Then_ReportsNoUnsupportedConstructs()
        {
            var xml = ReadEmbeddedResource("MilestoneSentryCase.cmmn");

            var importResult = CmmnXmlSerializer.Import(xml);
            importResult.IsError.Should().BeFalse(importResult.Message);

            var report = CmmnCapabilityLint.Lint(importResult.Value);

            report.HasFindings.Should().BeFalse();
        }

        private IPlanItemInternalGrain ResolveChildGrain(Guid caseInstanceId, CaseSnapshot caseSnapshot, string planItemId, string scope)
        {
            caseSnapshot.BehaviorExtension.Should().NotBeNull();
            caseSnapshot.BehaviorExtension.Children.Should().ContainKey(planItemId);

            var instanceId = caseSnapshot.BehaviorExtension.Children[planItemId].Keys.Single();

            return _clusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, $"{scope}.{instanceId}");
        }

        private static async Task<bool> PollUntil(Func<Task<bool>> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await condition()) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            return await condition();
        }

        private static string ReadEmbeddedResource(string suffix)
        {
            var assembly = typeof(CmmnImportDeployIntegrationTests).Assembly;
            var resourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix));
            using var stream = assembly.GetManifestResourceStream(resourceName);
            using var reader = new System.IO.StreamReader(stream!);
            return reader.ReadToEnd();
        }
    }
}
