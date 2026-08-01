using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using Wayfinder.Grains.Interchange;
using Wayfinder.Grains.Interfaces.Model.Interchange;

namespace Wayfinder.Benchmarks.Interchange
{
    // Design spec §4.2 (docs/specs/2026-08-01-benchmarks-design.md) - the CMMN interchange
    // round-trip. Unlike ExpressionBenchmarks' headline hypothesis, this one is included because
    // it is nearly free to write, not because anyone suspects it is slow: parsing happens once
    // per case-definition *deployment*, not per case or per rule evaluation, so it is the
    // lowest-stakes benchmark in this project. It exists to cheaply answer "will parsing ever
    // matter?", not to chase a hypothesis.
    public enum SampleModel
    {
        Small,
        Rich
    }

    [MemoryDiagnoser]
    public class CmmnInterchangeBenchmarks
    {
        [Params(SampleModel.Small, SampleModel.Rich)]
        public SampleModel Model { get; set; }

        private string _xml;
        private Definitions _definitions;

        // HONESTY NOTE (design spec §4.2): SmallSample.cmmn is ~2.0 KB and RichSample.cmmn is
        // ~7.9 KB - roughly a 4x spread, and neither is within an order of magnitude of a large
        // production model (dozens of stages/tasks, deep nesting, many sentries). These numbers
        // characterize the small end of the range only; they say nothing about how Import/Export
        // scale for a genuinely large .cmmn document.
        [GlobalSetup]
        public void GlobalSetup()
        {
            _xml = LoadEmbeddedSample(Model);

            // Correctness self-check, not a benchmark assertion: BenchmarkDotNet times the code
            // in [Benchmark] methods, and Import/Export both catch their own exceptions and
            // report failure via ExecutableResult rather than throwing (see CmmnXmlSerializer).
            // Nothing here would make a broken parse *fail to run* - it would just silently time
            // the catch-and-wrap path on every iteration and report numbers that look plausible
            // and mean nothing. Verify once, up front, that both directions actually succeed.
            var importResult = CmmnXmlSerializer.Import(_xml);
            if (importResult.IsError || importResult.Value is null)
            {
                throw new InvalidOperationException(
                    $"GlobalSetup: Import of {Model} sample failed round-trip self-check: " +
                    $"IsError={importResult.IsError}, Message='{importResult.Message}'");
            }

            _definitions = importResult.Value;

            var exportResult = CmmnXmlSerializer.Export(_definitions);
            if (exportResult.IsError || exportResult.Value is null)
            {
                throw new InvalidOperationException(
                    $"GlobalSetup: Export of {Model} sample failed round-trip self-check: " +
                    $"IsError={exportResult.IsError}, Message='{exportResult.Message}'");
            }
        }

        // Returns the parsed Definitions so the JIT cannot prove the call is dead and elide it -
        // BenchmarkDotNet's own return-value consumption handles this, but only if the method
        // actually returns the work product rather than discarding it into a local.
        [Benchmark]
        public Definitions Import() => CmmnXmlSerializer.Import(_xml).Value;

        // Deliberately serializes the SAME Definitions graph built in [GlobalSetup] on every
        // iteration - Export takes an already-parsed model, so there is no "fresh parse" step to
        // hoist out. This mirrors the production shape: Export is always called on a Definitions
        // instance that already exists (see design spec §5's per-iteration-construction
        // invariant), not one built as part of the timed work.
        [Benchmark]
        public string Export() => CmmnXmlSerializer.Export(_definitions).Value;

        // Resource names are mangled by the default namespace + folder path (e.g.
        // "Wayfinder.Benchmarks.Interchange.Samples.SmallSample.cmmn"), which is brittle to
        // guess and easy to get subtly wrong (culture suffixes, RootNamespace changes). Resolving
        // by suffix match against the actual manifest resource names avoids hard-coding that
        // mangled form, and throws with the full list if the match fails or is ambiguous, rather
        // than returning null/empty and letting a benchmark silently measure nothing.
        private static string LoadEmbeddedSample(SampleModel model)
        {
            var fileName = model switch
            {
                SampleModel.Small => "SmallSample.cmmn",
                SampleModel.Rich => "RichSample.cmmn",
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, null)
            };

            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();
            var matches = resourceNames
                .Where(name => name.EndsWith(fileName, StringComparison.Ordinal))
                .ToArray();

            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one embedded resource ending in '{fileName}', found " +
                    $"{matches.Length}. Available resources: [{string.Join(", ", resourceNames)}]");
            }

            using var stream = assembly.GetManifestResourceStream(matches[0]);
            if (stream is null)
            {
                throw new InvalidOperationException(
                    $"GetManifestResourceStream returned null for '{matches[0]}'. Available " +
                    $"resources: [{string.Join(", ", resourceNames)}]");
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
