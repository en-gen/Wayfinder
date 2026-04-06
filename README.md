# Case-Flow

A standards-compliant CMMN (Case Management Model and Notation) 1.1 engine built on Microsoft Orleans, implementing the OMG specification for knowledge-intensive, adaptive case management.

## What Is This?

Case-Flow is a distributed CMMN runtime engine. It implements the full CMMN 1.1 specification including:

- Case plan model and stage lifecycle management
- Human tasks, process tasks, and decision tasks
- Milestones and event listeners (timer, user)
- Sentry/criterion evaluation (entry and exit criteria)
- OnPart and IfPart evaluation with JavaScript expressions
- Manual activation, repetition, and required rules
- Multi-tenant case isolation via Orleans compound grain keys
- Event sourcing via Orleans JournaledGrain

## Architecture

The engine is built on [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/) using the virtual actor model. Each case instance, plan item, sentry, and definition is an independent Orleans grain, enabling horizontal scale across thousands of concurrent cases.

See [docs/02-codebase-evaluation.md](docs/02-codebase-evaluation.md) for a detailed architectural assessment.

## Project Structure

| Project | Purpose |
|---|---|
| `Flow.Grains.Interfaces` | Domain models, grain interfaces, snapshots |
| `Flow.Grains` | Core engine — grains, behaviors, state machines, stores |
| `Flow.Grains.Tests` | Unit tests (xUnit, Moq, AutoFixture) |
| `Flow.Grains.Tests.Integration` | Integration tests (Orleans TestingHost) |
| `Flow.Grains.Tests.Utils` | Shared test utilities and mocks |
| `Flow.Silo` | ASP.NET Core Orleans silo host |

## Technology Stack

- .NET Core 3.1 / netstandard2.1 *(target: .NET 8 — see modernization plan)*
- Microsoft Orleans 3.3.0 *(target: Orleans 8.x)*
- Stateless 5.1.3 (state machine)
- Quartz 3.1.0 (timer scheduling — to be replaced with Orleans Reminders)
- Orleans Reminders (currently grain keepalive only — to replace Quartz entirely)
- Jint 2.x (JavaScript expression evaluation — target: 3.x with System.Text.Json)
- Newtonsoft.Json *(to be replaced with System.Text.Json)*
- AutoMapper 10.x
- xUnit / Moq / AutoFixture / FluentAssertions

## Documentation

| Document | Description |
|---|---|
| [docs/01-cmmn-overview.md](docs/01-cmmn-overview.md) | CMMN standard overview and how concepts map to the engine |
| [docs/02-codebase-evaluation.md](docs/02-codebase-evaluation.md) | Architectural assessment — strengths, weaknesses, design decisions |
| [docs/03-modernization-plan.md](docs/03-modernization-plan.md) | Modernization roadmap and level-of-effort to production SaaS |
| [docs/04-market-analysis.md](docs/04-market-analysis.md) | Market landscape, strategic positioning, and use case analysis |
| [docs/05-business-case.md](docs/05-business-case.md) | Internal business case for adopting Case-Flow as a REDACTED product |

## Current Status

The engine implementation is substantially complete. The missing layers for a shippable product are:

- REST API (none exists — Orleans silo serves only `GET /` → `"Hello World"`)
- Production storage (Azure Storage clustering, durable grain state, durable streams)
- Authentication and tenant enforcement middleware
- CaseFileItem grain implementation (model exists, grain does not)
- Planning table (discretionary items — partially implemented)

## Building and Testing

```bash
dotnet build CaseFlow.sln
dotnet test CaseFlow.sln
```

Integration tests require no external dependencies — they use Orleans TestingHost with in-memory storage.
