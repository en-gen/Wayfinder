using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Concurrency;
using Orleans.Hosting;
using Orleans.Serialization;
using Wayfinder.Grains.Executables;
using Wayfinder.Grains.Interfaces.Model;

namespace Wayfinder.Benchmarks.Expressions
{
    // OQ-9 (docs/05-case-grain-redesign.md section D.2) - the half of the measurement the existing
    // harness does not cover.
    // ~~~~~
    // ExpressionEvaluationBenchmarks measures the Jint side. Its own header is explicit that the
    // other side is absent: "That envelope is out of scope for this harness ... and is UNMEASURED
    // here, not estimated." Section D.2 of the redesign doc nevertheless asserts that "everything
    // needed to compare Jint cost against local hop + serialization is in
    // src/Wayfinder.Benchmarks/Expressions/". That assertion was wrong when written. This class
    // makes it true.
    //
    // WHAT IS AND IS NOT MEASURED HERE. ExpressionGrain is [StatelessWorker], so in production it
    // is ALWAYS co-located with its caller - a StatelessWorker activation is created on the silo
    // that calls it. The relevant cost is therefore a LOCAL grain call, never a network one, and a
    // local Orleans call does not serialize to bytes: it deep-copies the arguments and the return
    // value through the registered codecs and dispatches through the scheduler. That deep copy is
    // the real serialization cost of this hop, and it is what these benchmarks capture. A
    // cross-silo number would be both larger and irrelevant to the decision.
    //
    // The probe grain below does NO Jint work at all - it returns a constant. That is the point:
    // with nothing to subtract, its time IS the envelope. Compare it directly against
    // ExpressionEvaluationBenchmarks.EvaluateAsWired (the production Jint shape) to answer OQ-9.
    //
    // Two argument shapes are measured, because the redesign changes the shape:
    //
    //   - TodayShapeHop: (string contextRef, Expression expression) -> ExecutableResult<bool>,
    //     exactly IExpressionGrain.ExecuteAsBool as it stands. Expression lives in
    //     Wayfinder.Grains.Interfaces.Model, so it is handled by the FALLBACK JSON codec
    //     (OrleansFallbackJsonSerializer.IsSupportedType matches that namespace), not by Orleans's
    //     native [GenerateSerializer] path. That is a materially more expensive copy and it is part
    //     of today's per-evaluation cost.
    //   - BoundShapeHop: the redesign's bound-request contract (section D.2) - a
    //     [GenerateSerializer] request type carrying the already-bound JSON, so the evaluator makes
    //     no outbound call. The bound JsonNode rides the native JsonCodec (see
    //     JsonNodeOrleansSerializationTests finding 1). Its cost scales with context size, which is
    //     exactly the knob OQ-9's third option (a size-threshold policy split) would switch on -
    //     hence the ContextProperties parameter.
    //
    // InProcessCall is the baseline: the same trivial work reached by a plain method call with no
    // Orleans involvement, i.e. what the in-process-service option costs.
    [MemoryDiagnoser]
    public class GrainCallEnvelopeBenchmarks
    {
        // Mirrors ExpressionEvaluationBenchmarks.ContextRefId - representative of a modeled
        // CaseFileItem name.
        private const string ContextRefId = "StatusItem";

        // 0 stands for no contextRef at all (the Trivial/Arithmetic shapes, where BuildExecutable
        // takes its early return and binds nothing), not an empty object. 1 is the shape
        // ExpressionEvaluationBenchmarks.BoundContext uses. 25 and 200 probe how the bound-request
        // cost scales, since that scaling - not the single-point cost - is what a threshold policy
        // would need.
        [Params(0, 1, 25, 200)]
        public int ContextProperties { get; set; }

        private IHost _host;
        private IHopProbeGrain _probe;
        private InProcessProbe _inProcess;

        private Expression _expression;
        private BoundExpressionRequest _boundRequest;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _expression = new Expression { Body = "value.amount > 100" };
            _boundRequest = new BoundExpressionRequest
            {
                Body = _expression.Body,
                ContextRef = ContextProperties == 0 ? null : ContextRefId,
                BoundValue = BuildContext(ContextProperties)
            };

            _inProcess = new InProcessProbe();

            // Ports derived from the process id so concurrent BenchmarkDotNet child processes (and
            // anything else on this machine) cannot collide. The silo is never stopped: the
            // benchmark process exits and takes it with it, which is cheaper and less flaky than
            // tearing a cluster down and rebuilding it per [Params] set.
            var portBase = 21000 + (Environment.ProcessId % 2000) * 2;

            _host = Host.CreateDefaultBuilder()
                .UseOrleans(silo =>
                {
                    silo.UseLocalhostClustering(
                        siloPort: portBase,
                        gatewayPort: portBase + 1,
                        primarySiloEndpoint: null);

                    // Registered identically to Wayfinder.Silo/Program.cs and ClusterFixture - see
                    // OrleansFallbackJsonSerializer's own remarks on why all registrations must
                    // match. Getting this wrong here would not fail loudly; it would quietly
                    // measure a different codec than production uses.
                    silo.Services.AddSerializer(s => s.AddJsonSerializer(
                        isSupported: OrleansFallbackJsonSerializer.IsSupportedType,
                        jsonSerializerOptions: OrleansFallbackJsonSerializer.Options()));
                })
                .Build();

            _host.StartAsync().GetAwaiter().GetResult();
            _probe = _host.Services.GetRequiredService<IGrainFactory>().GetGrain<IHopProbeGrain>(Guid.Empty);

            // Fail loudly rather than silently timing an error path - the same discipline
            // ExpressionEvaluationBenchmarks.GlobalSetup applies to its own self-check.
            var today = _probe.ProbeTodayShape(_boundRequest.ContextRef, _expression).GetAwaiter().GetResult();
            var bound = _probe.ProbeBoundShape(_boundRequest).GetAwaiter().GetResult();
            if (today.IsError || !today.Value || bound.IsError || !bound.Value)
            {
                throw new InvalidOperationException(
                    "probe self-check failed: " +
                    $"TodayShape(IsError={today.IsError}, Value={today.Value}) " +
                    $"BoundShape(IsError={bound.IsError}, Value={bound.Value})");
            }
        }

        [GlobalCleanup]
        public void GlobalCleanup() => _host?.StopAsync().GetAwaiter().GetResult();

        // The in-process-service option: no Orleans, no copy, no scheduler hop.
        [Benchmark(Baseline = true)]
        public bool InProcessCall() =>
            _inProcess.ProbeBoundShape(_boundRequest).GetAwaiter().GetResult().Value;

        // Today's IExpressionGrain.ExecuteAsBool argument shape. Expression rides the fallback JSON
        // codec.
        [Benchmark]
        public bool TodayShapeHop() =>
            _probe.ProbeTodayShape(_boundRequest.ContextRef, _expression).GetAwaiter().GetResult().Value;

        // The redesign's bound-request shape. Cost scales with ContextProperties.
        [Benchmark]
        public bool BoundShapeHop() =>
            _probe.ProbeBoundShape(_boundRequest).GetAwaiter().GetResult().Value;

        // A JSON object with n scalar properties, one of which is the amount the expression body
        // reads. n == 0 means no bound context at all.
        private static JsonNode BuildContext(int n)
        {
            if (n == 0)
            {
                return null;
            }

            var obj = new JsonObject { ["amount"] = 150 };
            for (var i = 1; i < n; i++)
            {
                obj["p" + i] = "v" + i;
            }

            return obj;
        }
    }

    // The redesign's bound-request contract (section D.2), in the shape P1 would introduce: the
    // caller binds the CaseFileItem value and passes it in, so the evaluator makes no outbound
    // grain call. [GenerateSerializer] rather than the fallback JSON codec, deliberately - the
    // point of measuring this shape is that it need not inherit the model namespace's fallback.
    [GenerateSerializer]
    public sealed class BoundExpressionRequest
    {
        [Id(0)]
        public string Body { get; set; }

        [Id(1)]
        public string ContextRef { get; set; }

        [Id(2)]
        public JsonNode BoundValue { get; set; }
    }

    public interface IHopProbeGrain : IGrainWithGuidKey
    {
        Task<ExecutableResult<bool>> ProbeTodayShape(string contextRef, Expression expression);

        Task<ExecutableResult<bool>> ProbeBoundShape(BoundExpressionRequest request);
    }

    // [StatelessWorker] to match ExpressionGrain exactly - the activation is created on the calling
    // silo, so this is the co-located local-call path production takes. It does no work on purpose:
    // whatever this costs is the envelope, with nothing to subtract.
    [StatelessWorker]
    public sealed class HopProbeGrain : Grain, IHopProbeGrain
    {
        public Task<ExecutableResult<bool>> ProbeTodayShape(string contextRef, Expression expression) =>
            Task.FromResult(ExecutableResult<bool>.Success(true));

        public Task<ExecutableResult<bool>> ProbeBoundShape(BoundExpressionRequest request) =>
            Task.FromResult(ExecutableResult<bool>.Success(true));
    }

    // The same method body, reached without Orleans. Task-returning on both sides of the
    // comparison, so the delta is the Orleans envelope and not Task machinery.
    public sealed class InProcessProbe
    {
        public Task<ExecutableResult<bool>> ProbeBoundShape(BoundExpressionRequest request) =>
            Task.FromResult(ExecutableResult<bool>.Success(true));
    }
}
