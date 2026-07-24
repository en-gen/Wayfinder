# Wayfinder

An agent-native, adaptive case & workflow engine — CMMN 1.1 implemented on Microsoft Orleans, event-sourced, multi-tenant.

## What Is This?

Most workflow engines make you draw the path in advance: a flowchart of steps, branches, and gates that the engine then marches through. That works for predictable, repeatable processes. It breaks down for knowledge-intensive work — case management, investigations, approvals, anything where the right next step depends on facts that only emerge as the work happens.

CMMN (Case Management Model and Notation) is the OMG standard for the other kind of process: **discretionary, adaptive work where the path is discovered at runtime, not fixed at design time.** A case defines the *possible* activities, the milestones that mark progress, and the sentries (event/condition rules) that govern when things become available — not a rigid sequence. Wayfinder is a distributed CMMN 1.1 runtime built on that model.

Each case instance, plan item, sentry, and definition is an independent Orleans grain (virtual actor), giving horizontal scale across thousands of concurrent cases with event-sourced state — every state transition is captured by construction, so a case's full history is always reconstructable, not just its current snapshot.

Implemented today:

- Case plan model, stage, and task lifecycles (state machines per CMMN §8.4)
- Model-driven case instantiation — `ICaseGrain.Create()` builds the plan-item tree from the CasePlanModel (discretionary items excluded, per §8.7 planning semantics)
- Human tasks, milestones, and event listeners (timer, user)
- Sentry/criterion evaluation — entry and exit criteria, onPart AND-semantics, ifPart conditions over case data, standalone-ifPart sentries, per-onPart re-arm across repetitions
- Case file items (§8.3) — full lifecycle, JSON content, and case-file events that drive sentries and timer start-triggers
- Planning tables, discretionary items, and applicability rules (risk-based tailoring)
- Manual activation, repetition, and required rules
- Expression evaluation with case-file context — conditions like `value.amount > 100` evaluate over the referenced case file item (sandboxed Jint)
- Multi-tenant case isolation via Orleans compound grain keys
- Event sourcing via Orleans JournaledGrain — a full audit trail by construction

Known gaps (tracked as work items; see the roadmap):

- A stage's bookkeeping of repeated child instances is incomplete — repetition works at the plan-item level, but the owning stage never learns of repeated instances
- ProcessTask / CaseTask / DecisionTask exist in the model but have no runtime behaviors
- HTTP ingress (`src/Flow.Api`, OData) is in progress — cases and definitions have initial endpoints, but coverage of the full engine surface is not complete yet
- No production deployment configuration (localhost clustering + volatile timers today)
- No `.cmmn` XML import/export (the XSD-generated model layer is serializer-ready; a structural importer is planned)

**Wayfinder does not claim OMG CMMN conformance** — see the project wiki's *Current State and Gaps* page for the honest scorecard.

### Why agent-native

The end goal is an engine that AI agents drive directly, not just humans through a UI — cases, tasks, and case-file data exposed through a protocol agents already speak (MCP), sitting on top of an API that's transport-agnostic by design (`Flow.Application` holds the CQRS command/query handlers; `Flow.Api` is one ingress onto them today, with MCP planned as a fast-follow rather than a rewrite).

## Naming — rename in progress

This project was recently renamed **Case.Flow → Wayfinder**. The rename is in flight: repository, issues, and docs now say Wayfinder, but the solution, projects, and namespaces have **not** been renamed yet — that's tracked as a separate follow-up PR. Until then, the actual paths and commands are still `src/CaseFlow.sln` and `Flow.*`, as documented below. Don't be surprised to see `Flow.Grains`, `Flow.Silo`, etc. — that's expected until the rename PR lands.

## Project Structure

| Path | Purpose |
|---|---|
| `src/Flow.Grains.Interfaces` | Domain model (XSD-generated CMMN metamodel + partials), grain interfaces, snapshots |
| `src/Flow.Grains` | Core engine — grains, behaviors, state machines, stores |
| `src/Flow.Silo` | ASP.NET Core Orleans silo host |
| `src/Flow.Api` | HTTP ingress (OData) |
| `src/Flow.Application` | Application layer — CQRS-style command/query handlers |
| `src/Flow.Contracts` | Shared request/response contracts |
| `src/Flow.Grains.Tests` | Unit tests (xUnit, Moq, AutoFixture) |
| `src/Flow.Grains.Tests.Integration` | Integration tests (Orleans TestCluster, in-memory) |
| `src/Flow.Grains.Tests.Utils` | Shared test utilities and mocks |
| `src/Flow.Api.Tests` | API layer tests |
| `devops/` | CI pipeline, deploy, and infrastructure (Docker Compose for local dependencies) |
| `docs/` | Design docs — CMMN overview, roadmap, market/business analysis, architecture evaluations |

## Technology Stack

- .NET 10 (LTS) — `global.json` pins the SDK
- Microsoft Orleans 10.2.1 — JournaledGrain event sourcing, memory streams, TestCluster
- System.Text.Json throughout (polymorphic CMMN model via a fail-closed `[XmlInclude]`-derived resolver)
- Stateless (plan-item lifecycle state machines)
- Quartz (timer scheduling; durable/clustered scheduling planned)
- Jint 4.x (JavaScript expression evaluation, sandboxed: timeout/statement/memory budgets)
- xUnit / Moq / AutoFixture / FluentAssertions

Dependency security is enforced at restore time: NuGet Audit fails any build with a moderate-or-higher advisory (direct or transitive), with nuget.org pinned as the sole package source.

## Documentation

- **Project wiki** — *what is built*: architecture, plan-item lifecycles, technical decisions, current state and gaps
- **Work items / [docs/07-product-roadmap.md](docs/07-product-roadmap.md)** — *what is planned*: milestones, expression-language strategy
- [docs/01-cmmn-overview.md](docs/01-cmmn-overview.md) — CMMN standard overview and concept mapping
- [docs/02-codebase-evaluation.md](docs/02-codebase-evaluation.md) — architectural assessment (historical snapshot, pre-modernization)
- [docs/03-modernization-plan.md](docs/03-modernization-plan.md) — modernization plan (historical)
- [docs/04-market-analysis.md](docs/04-market-analysis.md) — market landscape and positioning
- [docs/05-business-case.md](docs/05-business-case.md) — business case
- [docs/06-commercial-readiness-deep-dive.md](docs/06-commercial-readiness-deep-dive.md) — spec-adherence, quality, and market deep-dive
- [docs/08-orleans-provider-evaluation.md](docs/08-orleans-provider-evaluation.md) — Orleans storage/streaming provider evaluation

## Building and Testing

```bash
dotnet build src/CaseFlow.sln
dotnet test src/CaseFlow.sln
```

Requires the .NET 10 SDK. Most integration tests need no external dependencies — Orleans TestCluster with in-memory storage and streams. The build fails on vulnerable packages by design.

### Local development

Journaled-grain (case-file) state persists to Azure Blob Storage, backed locally by
[Azurite](https://github.com/Azure/Azurite):

```bash
docker compose -f devops/infrastructure/docker-compose.yml up -d
```

Then run the silo as usual — `Flow.Silo`'s Development configuration already points at
Azurite's well-known dev connection string.

The Azurite-dependent test suite (`Flow.Grains.Tests.Integration`'s `Storage` tests) is
unrelated to the compose stack above: it self-provisions its own throwaway Azurite container
per run via [Testcontainers](https://dotnet.testcontainers.org/), so it only needs Docker
running — no `docker compose up` and no fixed port to collide with a developer's own Azurite.
It skips itself automatically when Docker isn't reachable, so `dotnet test` works with or
without Docker running.

## Branching & Versioning

Wayfinder follows [GitFlow](https://nvie.com/posts/a-successful-git-branching-model/):

- `develop` is the default, integration branch — all feature work merges here first.
- `main` holds releases only, updated from `develop` via release branches.
- Feature branches: `feature/<work-item>_<short-description>`, off `develop`.
- Hotfix branches: `hotfix/<short-description>`, off `main`.

Versioning is computed by [GitVersion](https://gitversion.net/) — see [`GitVersion.yml`](GitVersion.yml)
for the exact branch-to-version rules (`develop` → `-develop.N` prereleases, `release/*` → `-rc.N`,
tagged `main` → the clean release version).
