using System;
using System.Text.Json.Nodes;
using Wayfinder.Grains.Executables;
using BenchmarkDotNet.Attributes;
using Jint;
using Microsoft.Extensions.Logging.Abstractions;

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
    // Isolated first: engine construction alone, nothing else in the mix.
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

    // Three expression shapes, chosen to separate parser/evaluator floor cost (Trivial) from
    // arithmetic (Arithmetic) from JSON context binding and JsonObjectInstance marshalling
    // (BoundContext) - see design spec section 4.1, item list.
    public enum ExpressionShape
    {
        Trivial,
        Arithmetic,
        BoundContext
    }

    // #224 section 4.1 / section 5 - THE CRITICAL INVARIANT lives in this class. Each benchmark below
    // must reproduce a specific, different object-construction shape, and the three shapes only mean
    // anything relative to each other:
    //
    //   - EvaluateAsWired is the production shape: AddRuleExecutor resolves a fresh transient Engine
    //     AND ExpressionGrain.BuildExecutable calls _executable(...) to build a fresh Executable, on
    //     every evaluation. Both constructions MUST happen inside the benchmark method body, per
    //     invocation. Moving either one into [GlobalSetup] does not "optimize" this benchmark - it
    //     silently turns it into EvaluateOnWarmEngine and erases the exact effect section 2 exists to
    //     measure. Do not hoist.
    //   - EvaluateWithReusedEngine reuses the Engine (built once, [GlobalSetup]) but still builds a
    //     new Executable per invocation - isolating what the transient registration alone costs,
    //     independent of Executable construction.
    //   - EvaluateOnWarmEngine reuses both Engine and Executable, built and (for BoundContext) bound
    //     once in [GlobalSetup] - the evaluation-only floor.
    [MemoryDiagnoser]
    public class ExpressionEvaluationBenchmarks
    {
        // ExpressionGrain binds a CaseFileItem's Value under two names simultaneously (see
        // ExpressionGrain.ValueArgumentName and BuildExecutable's two WithJsonArgument calls): the
        // spec-agnostic literal "value", and the contextRef's own id. This benchmark's contextRef
        // stand-in mirrors that - any identifier works for the id half, so "StatusItem" is chosen as
        // representative of a modeled CaseFileItem name.
        private const string ContextRefId = "StatusItem";

        [Params(ExpressionShape.Trivial, ExpressionShape.Arithmetic, ExpressionShape.BoundContext)]
        public ExpressionShape Shape { get; set; }

        private string _expressionBody;
        private JsonNode _boundContext;

        // Warm engine/executable for EvaluateWithReusedEngine and EvaluateOnWarmEngine. Built once
        // here, never inside a [Benchmark] method - that is what "reused"/"warm" means for those two.
        private Engine _warmEngine;
        private Executable _warmExecutable;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _expressionBody = BodyFor(Shape);
            _boundContext = Shape == ExpressionShape.BoundContext
                ? JsonNode.Parse("""{"amount":150}""")
                : null;

            _warmEngine = SandboxedJintEngine.Create();
            _warmExecutable = new Executable(_warmEngine, NullLogger<Executable>.Instance, _expressionBody);
            BindContextIfNeeded(_warmExecutable);

            // Every shape's body was chosen to evaluate to `true` (see BodyFor below). If a shape
            // does not - most likely because the BoundContext binding above does not actually
            // reproduce ExpressionGrain.BuildExecutable's shape - the benchmark would silently be
            // timing Jint's exception path (a caught TypeError, logged, ExecutableResult<bool>.Failure)
            // rather than its evaluation path, and every number downstream would describe the wrong
            // thing. Fail loudly instead of shipping that.
            var result = _warmExecutable.ExecuteAsBool();
            if (result.IsError || !result.Value)
            {
                throw new InvalidOperationException(
                    $"correctness self-check failed for {Shape} ('{_expressionBody}'): " +
                    $"IsError={result.IsError}, Value={result.Value}, Message={result.Message}");
            }
        }

        // Production shape (AddRuleExecutor + ExpressionGrain.BuildExecutable), baseline for the
        // other two. See THE CRITICAL INVARIANT above - Engine and Executable are constructed here,
        // inside the method body, on every invocation. Do not hoist either into [GlobalSetup].
        [Benchmark(Baseline = true)]
        public bool EvaluateAsWired()
        {
            var engine = SandboxedJintEngine.Create();
            var executable = new Executable(engine, NullLogger<Executable>.Instance, _expressionBody);
            BindContextIfNeeded(executable);
            return executable.ExecuteAsBool().Value;
        }

        // Same as EvaluateAsWired but against the Engine built once in [GlobalSetup] - quantifies the
        // delta the transient Engine registration costs, holding Executable construction constant.
        [Benchmark]
        public bool EvaluateWithReusedEngine()
        {
            var executable = new Executable(_warmEngine, NullLogger<Executable>.Instance, _expressionBody);
            BindContextIfNeeded(executable);
            return executable.ExecuteAsBool().Value;
        }

        // Evaluation only - both Engine and Executable (and, for BoundContext, the binding) already
        // exist from [GlobalSetup]. The floor: what Jint.Evaluate itself costs once nothing else is
        // being built.
        [Benchmark]
        public bool EvaluateOnWarmEngine() => _warmExecutable.ExecuteAsBool().Value;

        // Mirrors ExpressionGrain.BuildExecutable's `if (contextRef == null) return executor;` early
        // return: Trivial and Arithmetic have no ContextRef in the model, so no binding happens for
        // them, exactly as in production. Only BoundContext binds - and it binds under both names
        // BuildExecutable does, not just one, per THE CRITICAL INVARIANT.
        private void BindContextIfNeeded(IExecutable executable)
        {
            if (Shape != ExpressionShape.BoundContext)
            {
                return;
            }

            executable
                .WithJsonArgument("value", _boundContext)
                .WithJsonArgument(ContextRefId, _boundContext);
        }

        private static string BodyFor(ExpressionShape shape) => shape switch
        {
            ExpressionShape.Trivial => "true",
            ExpressionShape.Arithmetic => "1 + 1 > 1",
            ExpressionShape.BoundContext => "value.amount > 100",
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };
    }
}
