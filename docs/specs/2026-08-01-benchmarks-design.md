# Wayfinder.Benchmarks — design

**Issue:** [#224](https://github.com/en-gen/Wayfinder/issues/224)
**Date:** 2026-08-01
**Status:** approved, not yet implemented

## 1. Purpose

Wayfinder has no performance measurements. This project introduces a
[BenchmarkDotNet](https://benchmarkdotnet.org/) harness for **in-process, deterministic hot
paths** — code where nanoseconds and allocations are real numbers and a regression is
attributable to a specific commit.

It runs in **exploratory mode**: benchmarks exist to answer questions we have now, not to guard
numbers. There is no CI integration, no committed baseline, and no perf gate. Once measurements
identify which paths actually matter, individual benchmarks can be promoted to a regression
tripwire under a separate issue. That promotion is deliberately deferred — committing baselines
today would guard numbers we have no reason to believe are the important ones, and CI already
carries a ~6-minute job with a known-fragile integration suite.

### 1.1 What this is not

This is **not** [#34 (Benchmark report: 10k cases × 50 plan items)](https://github.com/en-gen/Wayfinder/issues/34).
That issue needs a load generator driving the API against a real silo and real storage. Three
reasons BenchmarkDotNet cannot serve it:

- Its statistical model assumes in-process, deterministic, side-effect-free work run millions of
  times. A grain call crossing the network into Azure Table Storage has variance measured in
  milliseconds; BenchmarkDotNet would report a mean and confidence interval that mean nothing.
- #34's question is scalability *under concurrency* (~500,000 concurrent `PlanItemGrain`
  activations, activation-storm behaviour). BenchmarkDotNet measures one operation serially. It
  structurally cannot express "10,000 concurrent cases."
- The metrics differ. BenchmarkDotNet yields ns/op and allocations. #34 needs p50/p99 latency,
  throughput, activation counts, and storage transaction cost.

#34 remains a separate track at milestone `0.3.0`, with its harness already designed in
[docs/08-orleans-provider-evaluation.md](../08-orleans-provider-evaluation.md) §7. It is also
worth substantially more once [#108 (OpenTelemetry traces + metrics)](https://github.com/en-gen/Wayfinder/issues/108)
lands, since without traces a load test yields a number but no explanation for it.

## 2. Leading hypothesis

`ServiceCollectionExtensions.AddRuleExecutor`
([src/Wayfinder.Grains/Infrastructure/Extensions/ServiceCollectionExtensions.cs:16](../../src/Wayfinder.Grains/Infrastructure/Extensions/ServiceCollectionExtensions.cs))
registers the Jint `Engine` as **transient**:

```csharp
.AddTransient(_ => SandboxedJintEngine.Create())
.AddSingleton<Func<string, IExecutable>>(sp =>
    expression => new Executable(sp.GetRequiredService<Engine>(), ...));
```

`ExpressionGrain.BuildExecutable` invokes that factory on every `ExecuteAsBool`,
`ExecuteAsString`, and `ExecuteAsIso8601` call, so a fresh JS realm with full intrinsics is
constructed **per evaluation** — for every sentry `IfPart`, `ManualActivationRule`,
`RequiredRule`, `RepetitionRule`, `ApplicabilityRule`, and timer expression.

**Hypothesis:** engine *construction* dominates expression *evaluation* by one to three orders of
magnitude, making it the true cost of rule evaluation in this engine.

This is **unverified**. It is a code-trace observation, not a measurement — the same standard the
`needs-repro` label applies to bug claims. Nothing acts on it until the benchmark produces
numbers. If it holds, it gets its own issue with those numbers attached.

## 3. Project layout

```
src/Wayfinder.Benchmarks/
  Wayfinder.Benchmarks.csproj
  Program.cs
  Expressions/ExpressionBenchmarks.cs
  Interchange/CmmnInterchangeBenchmarks.cs
  Interchange/Samples/*.cmmn          (embedded resources)
  Plan/StateMachineBenchmarks.cs
  Support/StubBehaviorStore.cs
```

- **Type:** console executable, `TargetFramework` `net10.0`, `LangVersion` `latest` — matching
  `Wayfinder.Grains.csproj`.
- **References:** `BenchmarkDotNet`, plus a `ProjectReference` to `Wayfinder.Grains`. Production
  code only.
- **Entry point:** `BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args)`, so
  individual benchmarks are selectable from the command line.

### 3.1 Solution integration

`Wayfinder.Benchmarks` **is added to `src/Wayfinder.sln`**. Consequences, all intended:

- CI compiles it on every build, so a breaking change to a `Wayfinder.Grains` signature fails
  loudly at PR time rather than rotting silently until someone next wants a number.
- It comes under `dotnet format --verify-no-changes` (ci.yml "Format check").
- It comes under the NuGet audit gate in [Directory.Build.props](../../Directory.Build.props):
  `NuGetAudit` with `NuGetAuditMode=all` and NU1902–NU1904 as errors. BenchmarkDotNet's transitive
  tree must therefore be advisory-clean, which is the correct bar for anything entering the
  solution.

Costs and non-effects:

- A few seconds of CI build time.
- Benchmarks are **never run** in CI. No step is added to `ci.yml`.
- Coverage is unaffected: `ci.yml` invokes `dotnet test` against three named test projects, and
  none of them reference this one.
- Minor wart: `src/**` is in `ci.yml`'s `push` path filter, so a benchmark-only push to `develop`
  triggers a full CI run.

### 3.2 Fixture data

Benchmark inputs live **inside the benchmarks project** as embedded resources. There is no
`ProjectReference` to any test project; that would couple performance work to test refactoring
and would drag xunit and Orleans test-host dependencies into a measurement process.

For CMMN models this means copying two representative fixtures in. Divergence from the test
fixtures is acceptable — benchmark inputs need to be *representative*, not *canonical*.

## 4. Benchmarks

### 4.1 `ExpressionBenchmarks` — the headline

`[MemoryDiagnoser]` enabled. Allocation is expected to be the more damning number here.

| Benchmark | What it isolates |
|---|---|
| `CreateEngine` | `SandboxedJintEngine.Create()` alone — realm + intrinsics construction |
| `EvaluateOnWarmEngine` | `Executable.ExecuteAsBool()` against an engine built in `[GlobalSetup]` |
| `EvaluateAsWired` | new engine + new `Executable` + evaluate — the production shape (baseline) |
| `EvaluateWithReusedEngine` | same work, engine reused — quantifies the delta |

`[Params]` over three expression bodies so cost attribution is separable:

1. `true` — parser and evaluation floor
2. `1 + 1 > 1` — arithmetic, no bound context
3. `value.amount > 100` with a bound `JsonNode` context via `WithJsonArgument` — isolates JSON
   binding and `JsonObjectInstance` marshalling cost

### 4.2 `CmmnInterchangeBenchmarks`

`CmmnXmlSerializer.Import(string)` over two model sizes: a small conformance-style sample and a
large one (`RichSample.cmmn` shape). `Export(Definitions)` is included as a second pair so the
interchange round-trip is covered.

This is the **deploy** path, not the per-case path, so the stakes are lower. It is included
because it is nearly free to write and it cheaply answers "will parsing ever matter?"

### 4.3 `StateMachineBenchmarks`

Two measurements, kept separate:

- **Construction** via `PlanItemStateMachineConfiguratorService.Configure(IBehaviorStore)` — all
  the Stateless `Permit`/`PermitIf` configuration. Paid once per `PlanItemGrain` activation, so
  roughly 500,000 times at #34's target shape. This is the number that matters.
- **Firing** a `Create → Enable → Start → Complete` sequence on an already-configured machine.

Requires `Support/StubBehaviorStore.cs`, a plain implementation of the eight-member
`IBehaviorStore` interface returning fixed values. No Orleans runtime, no grain activation.

### 4.4 Deliberately out of initial scope

`CmmnCapabilityLint`, `SnapshotMapper`, and Orleans serializer round-trips on the event types.
Add them once the first three show where the time actually goes. Adding benchmarks nobody has a
question about is how an exploratory harness turns into maintenance debt.

## 5. Correctness invariant

**Each benchmark must exercise the same object-construction shape as production wiring, or the
numbers are fiction.**

This is the easiest thing in the project to get subtly wrong, and a wrong benchmark is worse than
no benchmark because it is believed. Specifically:

- `EvaluateAsWired` must construct `Engine` and `Executable` per iteration, mirroring
  `AddRuleExecutor`'s transient registration. Hoisting either into `[GlobalSetup]` silently turns
  it into `EvaluateOnWarmEngine` and erases the very effect being measured.
- `Executable` takes an `ILogger<Executable>`. Benchmarks pass
  `NullLogger<Executable>.Instance`, matching production only in the success path — production
  logging cost on the *failure* path is not measured, and no benchmark should evaluate a throwing
  expression without saying so in its name.
- `StubBehaviorStore` must return a `PlanItemDefinition` shaped like a real one; a null or empty
  definition would let the configurator skip `Permit` registrations and understate construction
  cost.

## 6. Verification

Benchmarks are not tests and assert nothing, so "it passes" is not available as evidence. The
project is accepted when all of the following are demonstrated, with output:

1. `dotnet build src/Wayfinder.sln -c Release` succeeds with the new project in the solution.
2. `dotnet format src/Wayfinder.sln --verify-no-changes` reports no changes.
3. Restore succeeds with the NuGet audit gate active — no NU1902/NU1903/NU1904.
4. `dotnet run -c Release --project src/Wayfinder.Benchmarks -- --filter '*'` completes and emits
   a results table for every benchmark, with no BenchmarkDotNet validation errors or warnings
   about unstable measurements.
5. The recorded numbers are pasted into #224 as a comment, including the environment block
   (host OS, SDK, CPU) — a benchmark result without its environment is not interpretable.

Benchmarks must be run in **Release**. A Debug run is meaningless and BenchmarkDotNet will refuse
it by default.

## 7. Follow-on work

Deliberately not part of this issue:

- Any change to the transient-`Engine` registration. That waits for numbers, then gets its own
  issue.
- Promoting any benchmark to a CI regression gate with committed baselines.
- The #34 load-test harness.
- Feeding results into [#25 (FEEL spike)](https://github.com/en-gen/Wayfinder/issues/25). If §2's
  hypothesis holds, "feelin-on-Jint vs native .NET port" is measuring the wrong thing, and #25's
  framing needs revisiting before that spike is run.
