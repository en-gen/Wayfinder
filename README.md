# Case.Flow

A CMMN 1.1 case-management engine built on Microsoft Orleans — implementing the OMG specification's execution semantics for the case plan model, for knowledge-intensive, adaptive workflows.

## What Is This?

Case.Flow is a distributed CMMN runtime. Each case instance, plan item, sentry, and definition is an independent Orleans grain (virtual actor), giving horizontal scale across thousands of concurrent cases with event-sourced state.

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

- A stage's bookkeeping of repeated child instances is incomplete — repetition works at the plan-item level, but the owning stage never learns of repeated instances (Bug #62)
- ProcessTask / CaseTask / DecisionTask exist in the model but have no runtime behaviors
- No REST API yet — clients use the Orleans grain interfaces (M2)
- No production deployment configuration (localhost clustering + volatile timers; M2)
- No `.cmmn` XML import/export (the XSD-generated model layer is serializer-ready; a structural importer is planned)

**Case.Flow does not claim OMG CMMN conformance** — see the project wiki's *Current State and Gaps* page for the honest scorecard.

## Project Structure

| Path | Purpose |
|---|---|
| `src/Flow.Grains.Interfaces` | Domain model (XSD-generated CMMN metamodel + partials), grain interfaces, snapshots |
| `src/Flow.Grains` | Core engine — grains, behaviors, state machines, stores |
| `src/Flow.Silo` | ASP.NET Core Orleans silo host |
| `src/Flow.Grains.Tests` | Unit tests (xUnit, Moq, AutoFixture) |
| `src/Flow.Grains.Tests.Integration` | Integration tests (Orleans TestCluster, in-memory) |
| `src/Flow.Grains.Tests.Utils` | Shared test utilities and mocks |
| `devops/` | CI pipeline (`build/case.flow.ci.yml`), deploy + infrastructure (reserved) |

## Technology Stack

- .NET 10 (LTS) — `global.json` pins the SDK
- Microsoft Orleans 10.2.1 — JournaledGrain event sourcing, memory streams, TestCluster
- System.Text.Json throughout (polymorphic CMMN model via a fail-closed `[XmlInclude]`-derived resolver)
- Stateless (plan-item lifecycle state machines)
- Quartz (timer scheduling; durable/clustered scheduling planned for M2)
- Jint 4.x (JavaScript expression evaluation, sandboxed: timeout/statement/memory budgets)
- xUnit / Moq / AutoFixture / FluentAssertions

Dependency security is enforced at restore time: NuGet Audit fails any build with a moderate-or-higher advisory (direct or transitive), with nuget.org pinned as the sole package source.

## Documentation

- **Project wiki** — *what is built*: architecture, plan-item lifecycles, technical decisions, current state and gaps
- **Work items / [docs/07-product-roadmap.md](docs/07-product-roadmap.md)** — *what is planned*: milestones M0–M4, expression-language strategy
- [docs/01-cmmn-overview.md](docs/01-cmmn-overview.md) — CMMN standard overview and concept mapping
- [docs/02-codebase-evaluation.md](docs/02-codebase-evaluation.md) — architectural assessment (historical snapshot, pre-modernization)
- [docs/03-modernization-plan.md](docs/03-modernization-plan.md) — modernization plan (historical; executed as milestone M0)
- [docs/04-market-analysis.md](docs/04-market-analysis.md) — market landscape and positioning
- [docs/05-business-case.md](docs/05-business-case.md) — business case
- [docs/06-commercial-readiness-deep-dive.md](docs/06-commercial-readiness-deep-dive.md) — spec-adherence, quality, and market deep-dive

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
Azurite's well-known dev connection string. The Azurite-dependent test suite
(`Flow.Grains.Tests.Integration`'s `Storage` tests) skips itself automatically when Azurite
isn't running, so `dotnet test` works with or without the compose stack up.
