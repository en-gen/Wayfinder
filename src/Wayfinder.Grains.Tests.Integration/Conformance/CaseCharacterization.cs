using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Wayfinder.Grains.Interfaces.Plan.PlanItem.Behaviors;
using Wayfinder.Grains.Plan.PlanItem;
using Orleans;

namespace Wayfinder.Grains.Tests.Integration.Conformance
{
    // Issue #247 (P0 - characterization), docs/05-case-grain-redesign.md §E.3 "P0".
    // ~~~~~
    // A conformance scenario asserts the handful of points its spec citation is about. That is
    // the right shape for a citation test and the wrong shape for a BASELINE: if the case-grain
    // redesign (decision D-2026-08-01) moves some *other* plan item in the same tree from
    // Completed to Terminated, every existing assertion still passes and the change lands
    // silently. This renders the whole observable final state of a scenario's case instead, and
    // pins it against a committed baseline file, so an unintended change ANYWHERE in the tree
    // fails - not only at the points a scenario happened to cite.
    //
    // Deliberately a readable text rendering compared line-by-line, NOT a hash: the whole point
    // is that a reviewer looking at a failing build sees "PlanItemB Completed -> Terminated",
    // and that the baseline files themselves are reviewable in a diff when a change is
    // deliberate. A hash mismatch would tell a reviewer only that something moved.
    //
    // DETERMINISM. Two things vary per run and must never reach the rendering:
    //   * plan item INSTANCE ids are fresh Guids - so an instance is keyed by its definition id
    //     plus its repetition index ("PlanItemA#00"), never by its instance id, and sibling
    //     blocks are sorted by their own rendered text rather than by dictionary order.
    //   * the case id is "case-{ShortGuid}" and the case instance id is a fresh Guid - neither
    //     is rendered at all.
    // Everything rendered is therefore a function of the .cmmn sample and the operations the
    // scenario performed, which is exactly what P0 is trying to photograph.
    //
    // SETTLING. Behaviors settle asynchronously, so a snapshot taken the instant a scenario's
    // last assertion returns can be mid-cascade. Verify() polls until the rendering EQUALS the
    // baseline (returning the moment it does, so a green run costs nothing), and only then
    // compares once for the diff message. A run that is genuinely different pays the poll
    // timeout and then reports a readable diff.
    public static class CaseCharacterization
    {
        // Same budget every ConformanceHarness scenario already polls on - see
        // ConformanceHarness.DefaultTimeout's remarks (#149 precedent).
        private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

        // Set to a directory to have every rendering written out as "<key>.txt" - how the
        // committed baselines under Conformance/Baselines were generated, and how they are
        // regenerated after a DELIBERATE behavior change. Never read by CI.
        private const string OutputDirectoryVariable = "WAYFINDER_CHARACTERIZATION_OUT";

        public static async Task VerifyCharacterization(
            this ConformanceHarness harness,
            DeployedCase deployed,
            [CallerMemberName] string scenario = null,
            [CallerFilePath] string scenarioFile = null)
        {
            if (harness == null) throw new ArgumentNullException(nameof(harness));
            if (deployed == null) throw new ArgumentNullException(nameof(deployed));

            var key = $"{Path.GetFileNameWithoutExtension(scenarioFile)}.{scenario}";
            var expected = TryReadBaseline(key);

            var actual = expected == null
                ? await RenderWhenStable(harness, deployed)
                : await RenderUntilMatches(harness, deployed, expected);

            WriteOutputIfRequested(key, actual);

            if (expected == null)
            {
                throw new InvalidOperationException(
                    $"no committed characterization baseline for '{key}'. Add " +
                    $"Conformance/Baselines/{key}.txt (it is picked up by the csproj's " +
                    $"Conformance\\Baselines\\*.txt EmbeddedResource glob) with this content:" +
                    Environment.NewLine + Environment.NewLine + actual);
            }

            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(Describe(key, expected, actual));
            }
        }

        // The rendering itself - public so a one-off investigation can print it without going
        // through the baseline comparison.
        public static async Task<string> Render(ConformanceHarness harness, DeployedCase deployed)
        {
            var caseSnapshot = await deployed.CaseGrain.GetSnapshot();

            var builder = new StringBuilder();
            builder.Append("case state=").Append(caseSnapshot.PlanItemState)
                .Append(" userCompletable=").Append(caseSnapshot.UserCompletable)
                .Append(" required=").Append(caseSnapshot.Required)
                .Append(" repeatable=").Append(caseSnapshot.Repeatable)
                .Append(" repeated=").Append(caseSnapshot.Repeated)
                .Append('\n');

            builder.Append("plan\n");
            foreach (var line in await RenderChildren(harness, deployed.CaseInstanceId, caseSnapshot.BehaviorExtension, deployed.Scope, 1))
            {
                builder.Append(line).Append('\n');
            }

            builder.Append("casefile\n");
            foreach (var line in await RenderCaseFile(harness, deployed))
            {
                builder.Append(line).Append('\n');
            }

            return builder.ToString();
        }

        private static async Task<IReadOnlyList<string>> RenderChildren(
            ConformanceHarness harness, Guid caseInstanceId, StageBehaviorSnapshot stage, string scope, int depth)
        {
            if (stage?.Children == null) return Array.Empty<string>();

            var blocks = new List<List<string>>();

            foreach (var planItem in stage.Children)
            {
                foreach (var instance in planItem.Value)
                {
                    var address = $"{scope}.{instance.Key}";
                    var snapshot = await harness.ClusterClient
                        .GetGrain<IPlanItemInternalGrain>(caseInstanceId, address)
                        .GetSnapshot();

                    var block = new List<string>
                    {
                        new string(' ', depth * 2) + RenderPlanItem(planItem.Key, instance.Value, snapshot)
                    };

                    if (snapshot.BehaviorExtension is StageBehaviorSnapshot nested)
                    {
                        block.AddRange(await RenderChildren(harness, caseInstanceId, nested, address, depth + 1));
                    }

                    blocks.Add(block);
                }
            }

            // Sort by the block's OWN rendered text, not by dictionary order and not by instance
            // id: instance ids are fresh Guids, so any ordering derived from them is a different
            // baseline on every run. Ordinal so the ordering does not depend on culture.
            return blocks
                .OrderBy(block => string.Join("\n", block), StringComparer.Ordinal)
                .SelectMany(block => block)
                .ToList();
        }

        private static string RenderPlanItem(string planItemId, int repetition, Interfaces.Plan.PlanItem.PlanItemSnapshot snapshot) =>
            $"{planItemId}#{repetition:00} " +
            $"name={snapshot.Definition?.Name ?? "-"} " +
            $"type={snapshot.PlanItemDefinition?.GetType().Name ?? "-"} " +
            $"state={snapshot.PlanItemState} " +
            $"required={snapshot.Required} " +
            $"repeatable={snapshot.Repeatable} " +
            $"repeated={snapshot.Repeated} " +
            $"manuallyActivatable={snapshot.ManuallyActivatable} " +
            $"userCompletable={snapshot.UserCompletable} " +
            $"parentSuspendState={snapshot.ParentSuspendState?.ToString() ?? "-"}";

        // Every CaseFileItem the sample's <caseFileModel> declares, whether or not the scenario
        // ever created it - "not created" is itself a fact worth pinning (several samples declare
        // an item purely so a sentry has a sourceRef it can wait on forever).
        private static async Task<IReadOnlyList<string>> RenderCaseFile(ConformanceHarness harness, DeployedCase deployed)
        {
            var lines = new List<string>();

            foreach (var definition in Flatten(deployed.CaseModel?.CaseFileModel?.CaseFileItem))
            {
                var grain = harness.CaseFileItem(deployed.CaseInstanceId, definition.Id);

                if (!await grain.Defined())
                {
                    lines.Add($"  {definition.Id} name={definition.Name ?? "-"} (not created)");
                    continue;
                }

                var snapshot = await grain.GetSnapshot();
                lines.Add(
                    $"  {definition.Id} name={definition.Name ?? "-"} " +
                    $"state={snapshot.CaseFileItemState} " +
                    $"version={snapshot.CurrentVersion} " +
                    $"value={snapshot.Value?.ToJsonString() ?? "null"}");
            }

            return lines.OrderBy(line => line, StringComparer.Ordinal).ToList();
        }

        private static IEnumerable<Interfaces.Model.CaseFileItem> Flatten(IEnumerable<Interfaces.Model.CaseFileItem> items)
        {
            if (items == null) yield break;

            foreach (var item in items)
            {
                yield return item;

                foreach (var child in Flatten(item.Children?.CaseFileItem))
                {
                    yield return child;
                }
            }
        }

        private static async Task<string> RenderUntilMatches(ConformanceHarness harness, DeployedCase deployed, string expected)
        {
            var deadline = DateTime.UtcNow + SettleTimeout;
            var actual = await Render(harness, deployed);

            while (!string.Equals(expected, actual, StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150));
                actual = await Render(harness, deployed);
            }

            return actual;
        }

        // Only used when no baseline exists yet (the generation pass): settle on the rendering
        // staying identical across three consecutive reads rather than on matching anything.
        private static async Task<string> RenderWhenStable(ConformanceHarness harness, DeployedCase deployed)
        {
            var deadline = DateTime.UtcNow + SettleTimeout;
            var previous = await Render(harness, deployed);
            var stable = 0;

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
                var current = await Render(harness, deployed);

                stable = string.Equals(previous, current, StringComparison.Ordinal) ? stable + 1 : 0;
                previous = current;

                if (stable >= 2) break;
            }

            return previous;
        }

        private static string TryReadBaseline(string key)
        {
            var assembly = typeof(CaseCharacterization).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .SingleOrDefault(name => name.EndsWith($".{key}.txt", StringComparison.Ordinal));

            if (resourceName == null) return null;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            using var reader = new StreamReader(stream!);

            // Normalize to '\n' so a baseline file checked out with CRLF compares equal to a
            // rendering built with '\n'.
            return reader.ReadToEnd().Replace("\r\n", "\n");
        }

        private static void WriteOutputIfRequested(string key, string actual)
        {
            var directory = Environment.GetEnvironmentVariable(OutputDirectoryVariable);
            if (string.IsNullOrWhiteSpace(directory)) return;

            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{key}.txt"), actual);
        }

        // A line-oriented report: the first differing lines, then both full renderings. The
        // point of the first section is that a reviewer reads "PlanItemB ... state=Completed"
        // against "PlanItemB ... state=Terminated" without diffing 30 lines by eye.
        private static string Describe(string key, string expected, string actual)
        {
            var expectedLines = expected.Split('\n');
            var actualLines = actual.Split('\n');

            var report = new StringBuilder();
            report.Append("characterization baseline '").Append(key).Append("' no longer matches the engine's observable final state.").Append(Environment.NewLine);
            report.Append("If this change is DELIBERATE, regenerate Conformance/Baselines/").Append(key).Append(".txt and review the diff.").Append(Environment.NewLine);
            report.Append(Environment.NewLine).Append("differing lines:").Append(Environment.NewLine);

            for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
            {
                var expectedLine = i < expectedLines.Length ? expectedLines[i] : "<missing>";
                var actualLine = i < actualLines.Length ? actualLines[i] : "<missing>";

                if (string.Equals(expectedLine, actualLine, StringComparison.Ordinal)) continue;

                report.Append("  line ").Append(i + 1).Append(Environment.NewLine);
                report.Append("    expected: ").Append(expectedLine).Append(Environment.NewLine);
                report.Append("    actual:   ").Append(actualLine).Append(Environment.NewLine);
            }

            report.Append(Environment.NewLine).Append("full expected:").Append(Environment.NewLine).Append(expected);
            report.Append(Environment.NewLine).Append("full actual:").Append(Environment.NewLine).Append(actual);

            return report.ToString();
        }
    }
}
