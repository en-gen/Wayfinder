using System;
using System.Linq;
using System.Threading.Tasks;
using Wayfinder.Grains.Infrastructure.Extensions;
using Wayfinder.Grains.Interchange;
using Wayfinder.Grains.Interfaces;
using Wayfinder.Grains.Interfaces.Model;
using Wayfinder.Grains.Interfaces.Model.Interchange;
using Wayfinder.Grains.Interfaces.Plan.Case;
using Wayfinder.Grains.Interfaces.Plan.CaseFileItem;
using Wayfinder.Grains.Interfaces.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem;
using Orleans;

namespace Wayfinder.Grains.Tests.Integration.Conformance
{
    // ADO #21 - the conformance suite's shared runner.
    // ~~~~~
    // Every scenario in this suite is a REAL .cmmn file: imported via CmmnXmlSerializer, honesty-
    // checked via CmmnCapabilityLint, adapted via CaseInterchangeExtensions.ToDeployableCase, and
    // driven to a running case through the exact public front door
    // CmmnImportDeployIntegrationTests/CasePlanModelInstantiationIntegrationTests establish
    // (ICaseDefinitionGrain.Define -> ICaseGrain.Create -> ICaseGrain.Trigger). This class factors
    // that ceremony out of each individual scenario so a scenario test method reads as
    // arrange(load) -> act(drive) -> assert(spec citation), not import/lint/deploy boilerplate.
    //
    // Deliberately NOT a generic "operations interpreter": scenario methods call these helpers
    // directly and remain plain, readable C# - the ".cmmn file + operations + assertions" shape the
    // work item calls for lives in each scenario file's own linear method body (arrange from a named
    // sample, act via a short sequence of Trigger/CaseFileItem calls, assert with spec-citation
    // messages), not behind a second DSL a reader would have to learn first.
    public sealed class ConformanceHarness
    {
        // Generous by design: a poll returns the moment its condition holds, so this ceiling only
        // matters on a cold first run (JIT + TestCluster warm-up under the full 33-scenario
        // suite), where 10s proved marginal once during authoring.
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

        public IClusterClient ClusterClient { get; }

        public ConformanceHarness(IClusterClient clusterClient)
        {
            ClusterClient = clusterClient ?? throw new ArgumentNullException(nameof(clusterClient));

            // Matches every existing integration test's fixed tenant/user convention
            // (CmmnImportDeployIntegrationTests, CasePlanModelInstantiationIntegrationTests, et al.)
            // - a real multi-tenant caller would vary this; conformance is not testing tenancy.
            CaseRequestContext.TenantId = Guid.Parse("10000000-0000-0000-0000-000000000000");
            CaseRequestContext.UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        }

        // Reads a .cmmn file embedded under Conformance/Samples (see the csproj's EmbeddedResource
        // glob) by file-name suffix - mirrors CmmnImportDeployIntegrationTests.ReadEmbeddedResource
        // exactly, so a scenario's sample is loaded the same way the ADO #20 flagship test loads
        // its own.
        public static string ReadEmbeddedResource(string fileName)
        {
            var assembly = typeof(ConformanceHarness).Assembly;
            var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith(fileName));

            if (resourceName == null)
            {
                throw new InvalidOperationException(
                    $"no embedded resource ending in '{fileName}' - is it listed under the " +
                    "Conformance\\Samples\\*.cmmn EmbeddedResource glob in Wayfinder.Grains.Tests.Integration.csproj?");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            using var reader = new System.IO.StreamReader(stream!);
            return reader.ReadToEnd();
        }

        // Import + capability lint, with NO deployment yet - lets a scenario assert on the lint
        // report itself (the common case: assert it is clean, i.e. this scenario's sample uses
        // nothing the engine cannot faithfully run) before driving anything.
        public static (Definitions Definitions, CmmnCapabilityLintReport Lint) ImportAndLint(string fileName)
        {
            var xml = ReadEmbeddedResource(fileName);

            var importResult = CmmnXmlSerializer.Import(xml);
            if (importResult.IsError)
            {
                throw new InvalidOperationException($"{fileName} failed to import: {importResult.Message}");
            }

            var lint = CmmnCapabilityLint.Lint(importResult.Value);
            return (importResult.Value, lint);
        }

        // The standard "get a running case" ceremony: import, lint (throws if the sample uses a
        // construct this engine cannot run at all - a scenario-authoring error, not something a
        // conformance scenario should silently tolerate), deploy, Define, Create, and fire the
        // Create transition - exactly CmmnImportDeployIntegrationTests' sequence, reused for every
        // scenario in this suite instead of re-derived per file.
        public async Task<DeployedCase> DeployAndCreate(string fileName, string caseId = null)
        {
            var (definitions, lint) = ImportAndLint(fileName);

            if (lint.HasUnsupported)
            {
                throw new InvalidOperationException(
                    $"{fileName} uses a construct this engine cannot run at all: " +
                    string.Join("; ", lint.Findings.Select(f => f.ToString())));
            }

            var @case = definitions.ToDeployableCase(caseId);
            @case.Id = $"case-{ShortGuid.NewGuid()}"; // unique per test run; the sample's own id is fixed.

            var caseInstanceId = Guid.NewGuid();
            var scope = @case.CasePlanModel.Id;

            await ClusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, @case.Id)
                .Define(@case);

            var caseGrain = ClusterClient.GetGrain<ICaseGrain>(caseInstanceId, scope);
            await caseGrain.Create(@case.Id);

            var afterCreate = await caseGrain.Trigger(PlanItemTransition.Create);

            return new DeployedCase
            {
                CaseInstanceId = caseInstanceId,
                CaseDefinitionId = @case.Id,
                Scope = scope,
                CaseModel = @case,
                CaseGrain = caseGrain,
                Lint = lint,
                AfterCreateSnapshot = afterCreate
            };
        }

        // Deploy only (Define + Create) WITHOUT firing the Create transition - for the rare
        // scenario that needs to assert the pre-trigger (Uninitialized) state itself, mirroring
        // CasePlanModelInstantiationIntegrationTests.CaseTrigger__Given_CreateTransition__Then_
        // GoesDirectlyToActiveSkippingAvailable.
        public async Task<DeployedCase> Deploy(string fileName, string caseId = null)
        {
            var (definitions, lint) = ImportAndLint(fileName);

            if (lint.HasUnsupported)
            {
                throw new InvalidOperationException(
                    $"{fileName} uses a construct this engine cannot run at all: " +
                    string.Join("; ", lint.Findings.Select(f => f.ToString())));
            }

            var @case = definitions.ToDeployableCase(caseId);
            @case.Id = $"case-{ShortGuid.NewGuid()}";

            var caseInstanceId = Guid.NewGuid();
            var scope = @case.CasePlanModel.Id;

            await ClusterClient
                .GetGrain<ICaseDefinitionGrain>(CaseRequestContext.TenantId, @case.Id)
                .Define(@case);

            var caseGrain = ClusterClient.GetGrain<ICaseGrain>(caseInstanceId, scope);
            await caseGrain.Create(@case.Id);

            return new DeployedCase
            {
                CaseInstanceId = caseInstanceId,
                CaseDefinitionId = @case.Id,
                Scope = scope,
                CaseModel = @case,
                CaseGrain = caseGrain,
                Lint = lint,
                AfterCreateSnapshot = null
            };
        }

        // The full grain-key address (e.g. "CPM.ab12cd") of a child PlanItem instantiated under a
        // Stage/CasePlanModel snapshot, addressed by the PlanItem's OWN model id
        // (StageBehaviorStore.Children's key - see StageBehaviorStore's remarks: keyed by
        // ChildCreated.PlanItemId, the PlanItem's Id, not its DefinitionRef). Selects a specific
        // repetition (default 0 - the first/only instance).
        // Exposed separately from ResolveChild (below) so a caller can address a GRANDCHILD - a
        // nested Stage's own child - by passing this address back in as the next call's `scope`.
        public string ResolveChildAddress(StageBehaviorSnapshot behaviorExtension, string planItemId, string scope, int repetition = 0)
        {
            if (behaviorExtension?.Children == null || !behaviorExtension.Children.TryGetValue(planItemId, out var instances))
            {
                throw new InvalidOperationException(
                    $"no child instance of PlanItem '{planItemId}' has been created under scope '{scope}' yet");
            }

            var instanceId = instances.Where(kvp => kvp.Value == repetition).Select(kvp => kvp.Key).SingleOrDefault();

            if (instanceId == null)
            {
                throw new InvalidOperationException(
                    $"PlanItem '{planItemId}' has no repetition {repetition} instance under scope '{scope}' " +
                    $"(known repetitions: {string.Join(",", instances.Values)})");
            }

            return $"{scope}.{instanceId}";
        }

        public IPlanItemInternalGrain ResolveChild(
            Guid caseInstanceId, StageBehaviorSnapshot behaviorExtension, string planItemId, string scope, int repetition = 0) =>
            ClusterClient.GetGrain<IPlanItemInternalGrain>(caseInstanceId, ResolveChildAddress(behaviorExtension, planItemId, scope, repetition));

        // How many distinct instances (across all repetitions) the owning Stage/CasePlanModel has
        // recorded for a given PlanItem id - the direct observable for Bug #62 ("a stage's
        // bookkeeping of repeated child instances is incomplete"): a genuinely spawned second
        // repetition must show up here as count 2, keyed by two distinct instance ids.
        public int CountChildInstances(StageBehaviorSnapshot behaviorExtension, string planItemId) =>
            behaviorExtension?.Children != null && behaviorExtension.Children.TryGetValue(planItemId, out var instances)
                ? instances.Count
                : 0;

        public ICaseFileItemGrain CaseFileItem(Guid caseInstanceId, string caseFileItemId) =>
            ClusterClient.GetCaseFileItem(caseInstanceId, caseFileItemId);

        // Shared polling helper (mirrors the PollUntil every precedent integration test in this repo
        // hand-rolls per class) - streams/behaviors settle asynchronously, so scenario assertions
        // poll for the expected observable state rather than assuming synchronous completion.
        public static async Task<bool> PollUntil(Func<Task<bool>> condition, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
            while (DateTime.UtcNow < deadline)
            {
                if (await condition()) return true;
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            return await condition();
        }
    }

    // Everything a scenario needs after DeployAndCreate/Deploy: the running Case's addressing, its
    // deployed model (for reading back ids the sample itself declared), the CaseGrain to drive
    // further Case-level transitions on (Suspend/Terminate/Complete/Reactivate/Close), and the
    // snapshot captured immediately after Create (null if Deploy() was used instead).
    public sealed class DeployedCase
    {
        public Guid CaseInstanceId { get; init; }
        public string CaseDefinitionId { get; init; }
        public string Scope { get; init; }
        public Interfaces.Model.Case CaseModel { get; init; }
        public ICaseGrain CaseGrain { get; init; }
        public CmmnCapabilityLintReport Lint { get; init; }
        public CaseSnapshot AfterCreateSnapshot { get; init; }
    }
}
