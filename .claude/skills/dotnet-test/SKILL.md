---
name: dotnet-test
description: Build, test, and mutation-test Wayfinder. Use whenever running dotnet build/test/format on this repo, interpreting a suite result, or running Stryker. Covers the sandbox requirement, the two test projects and their expected counts, Docker-gated skips, husky in fresh worktrees, and the conformance corpus.
---

# Building and testing Wayfinder

## The one thing that will bite you first

**`dotnet build` and `dotnet test` require the sandbox to be DISABLED.** The machine's
NuGet config lives under `Program Files (x86)`, which a sandboxed process cannot read.
A sandboxed run fails at restore with an error that looks like a missing package, not a
permissions problem.

Solution file: `src/Wayfinder.sln`. Target framework `net10.0`, xUnit.

```bash
dotnet build src/Wayfinder.sln
dotnet format src/Wayfinder.sln --verify-no-changes
```

## The two test projects

| Project | Kind | Expected |
|---|---|---|
| `src/Wayfinder.Grains.Tests` | unit, fast | 442 passed |
| `src/Wayfinder.Grains.Tests.Integration` | Orleans TestCluster, ~2 min | 194 passed |

**Measured 2026-09-14 at `96661b1`** (the merge of #259, P1 of the case-grain redesign).
Both numbers moved sharply that day, so the full accounting is worth keeping:

| Change | Unit | Integration |
|---|---:|---:|
| Before the redesign phases | ~407 | 193 |
| #258 — P0 characterization (version numbering, `R<n>`) | — | +6 |
| #259 — P1 I4 binding tests | — | +4 |
| #259 — P1 architecture tests | +2 | — |
| #259 — `ExpressionEvaluator` + `CaseModelPin` unit tests | +33 | — |
| #256 — HTTP ingress removed: the 9-`[Fact]` `Api/` suite | — | −9 |
| **Now** | **442** | **194** |

`src/Wayfinder.Api.Tests` (44 tests) was deleted outright with the HTTP ingress under
D-2026-09-13, which is why this section names **two** test projects where it used to name
three. A run that still reports a third is on a branch predating #256.

`src/Wayfinder.Grains.Tests.Utils` is a helper library, not a test project.

Counts drift as work lands — treat them as a smell test, not a contract. A *large*
unexplained drop is worth investigating before you trust a green run.

## Docker-gated skips are not failures

The integration suite reports **194 passed / 0 skipped** when Docker containers are warm,
and **189 passed / 5 skipped** when they are not. The five are `RequiresDockerFact`-gated
Azurite / Azure-Table storage tests, which skip rather than fail when the probe finds no
daemon at discovery time.

A merely *responding* Docker daemon is not enough — the containers have to be warm. If you
see 179/5, that is almost certainly why. Re-run before reporting it as a regression, and
say which variant you saw when you quote a number.

## Fresh worktrees: restore husky before the first commit

A new worktree has no `.husky/_/husky.sh`, so the pre-commit hook dies with
`.husky/_/husky.sh: No such file or directory`. Fix it properly:

```bash
dotnet tool restore && dotnet husky install
```

**Never bypass with `--no-verify`.** The hook runs `dotnet format` on staged `.cs`/`.csproj`
files; on a docs-only commit it correctly reports "no matched files" and passes.

## Never `git stash`

The stash stack is **shared across every worktree of this repo**, and other sessions may be
working concurrently. A `git stash` here can sweep up someone else's in-flight work — this
has actually happened. Commit WIP instead. Stage explicit paths; never `git add -A`.

## The conformance corpus is the acceptance gate

`src/Wayfinder.Grains.Tests.Integration/Conformance/` holds **31 `.cmmn` samples driving 41
`[Fact]` scenarios** across `CaseFileScenarios`, `InstantiationScenarios`, `KnownGapScenarios`,
`LifecycleScenarios`, and `SentryScenarios`. It tests the public surface, so it survives
internal refactoring — which is why decision D-2026-08-01 makes it the acceptance gate for
the case-grain redesign.

Run it alone:

```bash
dotnet test src/Wayfinder.Grains.Tests.Integration \
  --filter "FullyQualifiedName~Wayfinder.Grains.Tests.Integration.Conformance"
```

`Conformance/COVERAGE.md` carries an **explicit honesty rule**: a row may only be marked
`Pinned` if a committed, running test actually pins it. Do not mark a row green because the
behaviour looks right — that file has drifted twice by exactly that route.

# Mutation testing with Stryker

Stryker answers the question a passing suite cannot: *do these tests actually detect a
defect, or do they merely execute the code?* Coverage says a line ran. Mutation says a
test would have noticed it changing.

There are **two configs**, because one cannot serve both jobs.

## Day-to-day: the fast one

```bash
dotnet tool restore
dotnet stryker                    # stryker-config.json
```

Mutates the CMMN semantics layer — behaviors, state machine, sentries — and kills those
mutants with the **unit suite**. Minutes rather than hours, so it is the one that fits a
dev loop.

Measured reference point, so you can tell a healthy run from a broken one: mutating
`MilestoneBehavior.cs` alone produced 27 testable mutants — **23 killed, 4 survived,
71.88%** — in about 30 seconds. The full committed scope is substantially more mutants;
time it on your own machine rather than trusting a number here.

## The acceptance-gate measurement: the slow one

```bash
dotnet stryker --config-file stryker-conformance.json
```

Same mutants, killed by the **41-scenario conformance corpus** instead. This is the number
that matters for D-2026-08-01: the corpus is the acceptance gate for the case-grain
redesign, and this measures whether that gate has teeth before the redesign is judged
against it.

Measured cost: **~3.3 seconds per mutant** at `concurrency: 2`, plus about a minute of
build-and-initial-test startup. Runtime is therefore driven by how much you mutate, not by
a fixed penalty — time a narrow run before launching the full scope.

Reference result: `MilestoneBehavior.cs` alone gave **32 mutants, 32 killed, 0 survived —
100.00%** in 3m02s. The corpus genuinely detects changes to milestone semantics rather than
merely executing them.

## `coverage-analysis` MUST stay `off` for the conformance config

This is measured, not theoretical. Against the Orleans TestCluster harness, **both**
`perTest` and `all` report every mutant as `NoCoverage`, so Stryker tests nothing and
reports a **false 0.00% score** that looks like catastrophic test quality:

```
32   mutants got status NoCoverage.   Reason: Not covered by any test.
0    total mutants will be tested
The final mutation score is 0.00 %
```

The identical mutants against the unit suite with `perTest` give 23 killed / 4 survived.
So the corpus is not the problem — coverage instrumentation does not survive the test
cluster. With `coverage-analysis: off` every mutant runs all 41 scenarios, which is why
that config is slow and why the slowness is not negotiable.

**If you ever see a 0.00% score with a large `NoCoverage` count, suspect this before you
suspect the tests.**

## Reading the result

- **Survived** = the code changed and every test still passed. A hole in the *assertions*,
  not necessarily a bug — it means a rule is unpinned.
- **Killed** = at least one test failed. Genuinely pinned.
- **Timeout** counts as killed (the mutant probably caused a loop), but a lot of timeouts
  usually means `additional-timeout` is too tight for this suite, not that tests are strong.
- **NoCoverage** = nothing exercises that code. See the warning above first.
- **CompileError** ≈ 116 of 1889 mutants — **measured 2026-08 on the pre-redesign tree, and
  not re-measured since.** #259 deleted `ExpressionGrain` and added `ExpressionEvaluator` and
  `CaseModelPin`, so the mutant totals below have certainly drifted; the *ratios* and the
  reasoning still hold, the absolute numbers are stale. Re-run the sweep before quoting them. Stryker's "safe mode" discards every mutant
  in a method whose mutation will not compile (two methods trip this: `StageBehavior.Define`
  and `CmmnXmlSerializer.Walk`). Expected, not a misconfiguration.

The score is not a target. `thresholds.break` is `0` deliberately so a run never fails a
build. A survived mutant in the behaviors layer is worth more than the headline number.

## Concurrency is pinned to 2 on purpose

The integration fixtures each stand up an Orleans TestCluster, xUnit collection parallelism
is already disabled, and over-subscribing CPU has previously produced spurious failures on
this suite. Raising it to "speed things up" makes runs *less* reliable — and a flaky test
under mutation reads as a falsely-killed mutant, which is worse than a slow run.

## Narrowing a run

Edit `mutate`, or override for a one-off:

```bash
dotnet stryker --mutate "**/Plan/PlanItem/Behaviors/StageBehavior.cs"
```

Note `--test-project` does **not** override the config's `test-projects`, and CLI
`--test-case-filter` does not reliably clear a configured one. To point mutants at a
different suite, copy a config file and pass `--config-file`.

Output lands in `StrykerOutput/` (gitignored). Open the HTML report; the JSON is for tooling.
