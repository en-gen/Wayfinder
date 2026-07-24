# Contributing to Wayfinder

Thank you for your interest in contributing. This document explains how to get involved.

> **Wayfinder is source-available, not open source.** It is licensed under the
> [Business Source License 1.1](LICENSE). Read the [Licensing of contributions](#licensing-of-contributions)
> section before you open a pull request — the terms differ from a typical MIT/Apache project.

## Before you start

Open a GitHub Issue before writing code. This gives the maintainer a chance to discuss the
approach, flag any conflicts with the roadmap, and avoid wasted effort. You are welcome to
attach a draft PR to the issue if you want to show your direction early, but it is not
required before discussion.

For small typo or doc fixes you can skip the issue and go straight to a PR.

Because this is a CMMN 1.1 implementation, changes to engine behaviour should cite the
relevant clause of the OMG specification (for example `§8.4.2`) in the issue or PR
description. "The spec says so" is a valid and preferred argument here; "it seems more
intuitive" usually is not.

## Development setup

**Prerequisites:** the .NET 10 SDK (pinned in `global.json`) and Docker Desktop.

```bash
git clone https://github.com/en-gen/Wayfinder.git
cd Wayfinder

dotnet build src/CaseFlow.sln
dotnet test src/CaseFlow.sln
```

Most integration tests need no external dependencies — they run an Orleans `TestCluster`
with in-memory storage and streams. The Azurite-backed storage suite self-provisions its
own throwaway container through Testcontainers, so it only needs Docker running, and skips
itself automatically when Docker is unreachable.

For running the silo locally against persistent storage:

```bash
docker compose -f devops/infrastructure/docker-compose.yml up -d
```

`Flow.Silo`'s Development configuration already points at Azurite's well-known dev
connection string.

> **Note on names:** the project was renamed Case.Flow → Wayfinder. The solution, projects,
> and namespaces have not been renamed yet, so paths are still `src/CaseFlow.sln` and
> `Flow.*`. That rename is tracked as a separate change.

## Branching

This repo uses GitFlow. Branch off `develop` for all contributions.

| Type | Prefix | Example |
|------|--------|---------|
| New feature | `feature/` | `feature/process-task-behavior` |
| Bug fix | `bugfix/` | `bugfix/sentry-rearm-on-repetition` |
| Maintenance | `chore/` | `chore/bump-orleans` |

Target `develop` in your PR. PRs targeting `main` directly will be declined.

## Code style

Code style is enforced automatically. A pre-commit hook runs `dotnet format` before every
commit. If the hook rejects your commit, run:

```bash
dotnet format src/CaseFlow.sln
```

and re-commit. CI runs the same check and will fail if style is off.

A few conventions to be aware of:

- `ImplicitUsings` is **not** enabled. Add all `using` statements explicitly in every `.cs` file.
- Match the surrounding file's namespace and brace style rather than reformatting it.
- Public grain interfaces and behaviours should carry XML documentation that references the
  CMMN clause they implement.
- Dependency security is enforced at restore time: NuGet Audit fails the build on any
  advisory at `low` or above, transitive dependencies included. Do not suppress it — take
  the upgrade, or raise an issue if no fixed version exists.
- Stable dependencies only. No preview, RC, or beta packages.

## Pull requests

- Keep each PR focused on a single concern.
- Include or update tests for any behaviour change. Tests must assert on real observable
  effects — a test that only proves the code ran is not sufficient.
- Changes to engine semantics should add or update a scenario in the conformance suite
  (`src/Flow.Grains.Tests.Integration/Conformance`) and its `COVERAGE.md` row.
- The PR description should explain *why* the change is needed, not just what it does.
- All CI checks must pass before review.

## Licensing of contributions

Wayfinder is distributed under the [Business Source License 1.1](LICENSE), which converts to
Apache License 2.0 on the Change Date. The maintainer also offers the software under separate
commercial terms.

By submitting a pull request you agree that:

1. Your contribution is licensed under the same Business Source License 1.1 that covers this
   project; and
2. You grant the maintainer a perpetual, worldwide, non-exclusive, royalty-free, irrevocable
   licence to relicense your contribution under other terms, including commercial and
   proprietary licences.

Point 2 exists because the project is dual-licensed. Without it, a single contribution under
inbound-only terms would make the commercial licence unofferable. If you are contributing on
behalf of an employer, make sure you have the authority to grant this.

## Releasing

This section is for maintainers.

1. Merge all intended changes into `develop` via PRs.
2. Create a `release/x.y.z` branch from `develop`.
3. Update `CHANGELOG.md`: rename `[Unreleased]` to `[x.y.z] - YYYY-MM-DD` and add a fresh
   `[Unreleased]` section above it.
4. Open a PR from `release/x.y.z` → `main`. Merge once CI is green.
5. On GitHub, create a **Release** targeting `main`. The tag must match what GitVersion
   computes from the branch history — see `tag-prefix` in `GitVersion.yml`.
6. Publishing the release triggers the CD workflow, which builds and pushes the silo
   container image to GitHub Container Registry.
7. Merge `main` back into `develop`.

## Reporting bugs

Open an issue using the Bug Report template. Include a minimal reproduction if possible —
for engine bugs, the most useful reproduction is a small `.cmmn` file plus the sequence of
transitions that produces the wrong state.

Do not open issues for security vulnerabilities; see [SECURITY.md](SECURITY.md).
