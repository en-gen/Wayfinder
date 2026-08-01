using System;
using System.Text.Json.Nodes;
using Wayfinder.Grains.Executables;
using BenchmarkDotNet.Attributes;
using Jint;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wayfinder.Benchmarks.Expressions
{
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
    //
    // Omitted DI resolutions (#224 review): production's _executable(expression.Body) call runs the
    // AddRuleExecutor factory registered in ServiceCollectionExtensions.cs, which performs
    // sp.GetRequiredService<Engine>() (transient - this invokes the factory, i.e. is the same
    // construction EvaluateAsWired already models) AND sp.GetRequiredService<ILogger<Executable>>()
    // - a container resolution - on every single evaluation. EvaluateAsWired below calls
    // SandboxedJintEngine.Create() directly and passes NullLogger<Executable>.Instance as a field
    // reference, so both container-resolution calls themselves (as opposed to the Engine/Executable
    // construction they trigger) are absent from every benchmark in this class. This is a
    // conservative omission: it can only make the transient-Engine effect measured here look
    // SMALLER than production's, never larger, since the missing DI resolutions are pure additional
    // cost EvaluateAsWired does not pay.
    //
    // Grain-call envelope (#224 review): ExpressionGrain is [StatelessWorker], so every real rule
    // evaluation is also an Orleans grain call - serialization of the Expression argument in and the
    // ExecutableResult<T> return value out. That envelope is out of scope for this harness (design
    // spec §1.1 - BenchmarkDotNet cannot usefully time network/serialization variance) and is
    // UNMEASURED here, not estimated. A reader taking EvaluateAsWired vs EvaluateWithReusedEngine off
    // the table and concluding "fix the transient registration, get ~4x on rule evaluation" is
    // over-reading it: if the grain-call envelope is comparable to or larger than the in-process
    // delta measured here, the reachable win from fixing the registration is a fraction of that
    // ratio, not the ratio itself. Treat the ratio as an upper bound on the achievable win. This
    // matters concretely because design spec §7 says the transient-registration decision waits for
    // these numbers.
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
        //
        // NullLogger caveat (mirrors StateMachineBenchmarks.cs's logging caveat): Executable logs on
        // its catch-and-log failure path (`_logger.LogError(e, "unable to evaluate expression: ...")`
        // - see Executable.cs), and every benchmark in this class passes
        // NullLogger<Executable>.Instance, matching production only on the success path. None of the
        // three shapes below throws (see the GlobalSetup self-check), so this never exercises that
        // path - production logging cost on a FAILING expression is UNMEASURED by this class, not
        // small, not estimated.
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
