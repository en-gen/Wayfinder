using Wayfinder.Grains.Executables;
using BenchmarkDotNet.Attributes;
using Jint;

namespace Wayfinder.Benchmarks.Expressions
{
    // #224 section 4.1 - the headline benchmark. AddRuleExecutor
    // (src/Wayfinder.Grains/Infrastructure/Extensions/ServiceCollectionExtensions.cs) registers the
    // Jint Engine as transient, so ExpressionGrain.BuildExecutable builds a brand-new realm on every
    // single sentry IfPart / ManualActivationRule / RequiredRule / RepetitionRule / ApplicabilityRule
    // evaluation. Section 2's hypothesis is that engine construction dominates evaluation by one to
    // three orders of magnitude - this class is what turns that from a code-trace observation into a
    // number.
    //
    // Isolated first: engine construction alone, nothing else in the mix. See
    // ExpressionEvaluationBenchmarks.cs (same folder) for the class this one used to share a file
    // with - the two were split per #224 review because the filename matched neither class and the
    // other two benchmark files each match their class name exactly.
    [MemoryDiagnoser]
    public class JintEngineBenchmarks
    {
        // The return value matters as much as the body: a method that builds an Engine and discards
        // it is exactly the shape the JIT is entitled to eliminate as dead code once it can prove
        // nothing observes the result. Returning the Engine gives BenchmarkDotNet's consumer plumbing
        // something to hold onto, which is what keeps SandboxedJintEngine.Create() from being
        // optimized away.
        [Benchmark]
        public Engine CreateEngine() => SandboxedJintEngine.Create();
    }
}
