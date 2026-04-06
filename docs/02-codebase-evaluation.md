# Codebase Evaluation

*Assessment conducted April 2026. Engine originally authored ~2015-2016.*

---

## Summary

Case-Flow is a substantially complete CMMN 1.1 engine with solid architectural instincts. The core design decisions — Orleans virtual actors, JournaledGrain event sourcing, behavior pattern for testability — were correct and hold up well. The weaknesses are typical of code written at a different point in one's career: over-abstraction in some places, duplication in others, and implicit coupling that wasn't visible at the time.

The engine is worth modernizing rather than rewriting. The domain logic and event model are the hardest parts and they are sound.

---

## Codebase Metrics

| Project | Files | Lines |
|---|---|---|
| Flow.Grains.Interfaces | 30 | 3,606 |
| Flow.Grains | 101 | 4,705 |
| Flow.Grains.Tests | 25 | 6,928 |
| Flow.Grains.Tests.Integration | 8 | 945 |
| Flow.Grains.Tests.Utils | 4 | 235 |
| Flow.Silo | 4 | 172 |
| **Total** | **172** | **16,591** |

Engine code: ~8,300 lines. Test code: ~8,100 lines. Healthy ratio.

---

## What Was Done Well

### Orleans + JournaledGrain Was the Right Choice

The virtual actor model maps naturally to CMMN. Each case instance is isolated state; timers are reminders; event-driven sentry evaluation fits streams. Using `JournaledGrain<TState>` for event sourcing means every state change is captured as an immutable event — full audit trail, replayable history, correct by construction.

### Behavior Pattern Enables Testability

The `IBehaviorHost` adapter that decouples grain behavior from Orleans infrastructure is genuinely good design. Behaviors can be unit-tested without spinning up an Orleans cluster. The 42-file unit test suite is a direct consequence of this decision. Most Orleans codebases don't do this.

### CMMN Spec Fidelity

The code contains extensive `// SPEC:` comments mapping implementation to the OMG specification. The criterion/sentry/onPart structure faithfully reflects CMMN 1.1. The state machine transitions match the spec's lifecycle diagrams.

### JavaScript Expressions via Jint

FEEL (the CMMN/DMN expression language) would have been more spec-correct but significantly harder to implement. Jint was a pragmatic tradeoff that works and is familiar to any developer.

### Orleans Reminders for Grain Keepalive

`TimerEventSchedulerGrain` implements `IRemindable` and registers an Orleans Reminder every 118 minutes (just under the default grain inactivity timeout). This prevents the scheduler grain from being deactivated between timer firings. The pattern is correct.

### Compound Grain Keys for Multi-Tenancy

Grain keys structured as `(TenantId, Address)` bake multi-tenancy into the grain addressing scheme. Each tenant's case data is isolated at the actor level without a shared database schema trick.

### Hierarchical Address Encoding

Grain addresses encode parent-child relationships: `caseId.stageId.taskId`. This enables scope-based event filtering — grains only process events from within their own hierarchy — without cross-cutting concerns.

---

## What Should Be Revisited

### `CaseRequestContext` — Ambient Static State (Critical)

```csharp
public static class CaseRequestContext
{
    public static Guid TenantId { get; set; }
    public static Guid UserId { get; set; }
    public static IEnumerable<string> UserRoles { get; set; }
}
```

22 references across the codebase. Threading tenant/user through a static ambient context is an anti-pattern:
- Invisible coupling — dependencies aren't declared, they're assumed
- In async grain execution, execution context can bleed between activations
- Makes testing require global setup/teardown
- Security footgun — a missed reset could leak tenant context

**Fix:** Pass tenant/user context explicitly. In Orleans, grain activation arguments or a scoped `AsyncLocal<T>` are the right patterns.

### `IBehaviorHost` — God Interface

```csharp
public interface IBehaviorHost {
    Guid CaseInstanceId { get; }
    string Address { get; }
    string Scope { get; }
    IBehaviorDefinition Definition { get; }
    IBehaviorStore State { get; }
    IGrainFactory GrainFactory { get; }  // Orleans infrastructure leaking into domain
    void RaiseEvent<TEvent>(TEvent @event);
    Task SubscribeTo<TEvent>(...);
    // ... 6 more members
}
```

14 members mixing grain plumbing (`GrainFactory`, `Scope`, `Address`) with domain concerns (`Definition`, `State`, `RaiseEvent`). This violates Interface Segregation — `TaskBehavior` is forced to depend on `GrainFactory` it never uses. The interface grows over time because it's always "easier to add one more thing."

**Fix:** Split into `IDomainBehaviorHost` (domain concerns) and `IChildManagementBehaviorHost : IDomainBehaviorHost` (grain factory + addressing, for stages only).

### `StageBehavior` and `TaskBehavior` — ~80% Duplicated

The sentry-satisfaction handling and parent-transition handling are nearly identical between `StageBehavior` (432 lines) and `TaskBehavior` (242 lines). This is the classic inheritance-over-composition trap — a hierarchy was built but the actual behavior was copy-pasted between siblings.

```csharp
// StageBehavior.HandleSentrySatisfied lines 127-205
// TaskBehavior.HandleSentrySatisfied lines 182-207 — nearly identical
```

**Fix:** Extract common sentry-satisfaction logic into composable policies (`CriterionSatisfactionPolicy`, `ManualActivationPolicy`) used by both behaviors.

### `BaseBehavior<TPlanItemDefinition>` — Generics That Don't Earn Their Complexity

The type parameter is used only to constrain the definition type — it doesn't change runtime behavior, enable reuse, or prevent bugs in any meaningful way. It makes the type hierarchy harder to read and refactor.

**Fix:** Remove the generic parameter; use a base definition type or interface.

### LINQ Expression Compilation in Hot Path

Criteria predicates are compiled on every evaluation in `BaseBehavior`:
```csharp
criteria.Compile().Invoke(Host.Definition)
```
Compiled LINQ expressions are expensive. This runs every time a sentry is evaluated — the hot path for case execution.

**Fix:** Cache compiled predicates during `Define()`.

### `ConfigureDeployedOrleans()` Throws `NotImplementedException`

```csharp
private static void ConfigureDeployedOrleans(HostBuilderContext context, ISiloBuilder silo)
{
    throw new NotImplementedException();
}
```

The production deployment path was never implemented. This engine was only ever run in development mode — in-memory clustering, in-memory grain state, volatile Quartz scheduler. No case state was ever durably persisted.

---

## Key Design Decisions — Assessment

| Decision | Assessment |
|---|---|
| Orleans virtual actors for case instances | Correct. Scales well, natural isolation. |
| JournaledGrain event sourcing | Correct. Audit trail, replayable state. |
| Behavior pattern (separate from grain) | Correct. Enables unit testing. |
| Stateless library for state machines | Correct. Mature, readable configuration. |
| JavaScript (Jint) for expressions | Pragmatic. Not spec-compliant (FEEL) but workable. |
| Quartz for timer execution + Orleans Reminders for keepalive | Functional but unnecessary complexity. Quartz should be replaced with full Orleans Reminder-based scheduling. |
| Compound grain keys for multi-tenancy | Correct. Baked in at the right level. |
| `[Serializable]` for event/state classes | Outdated. Orleans 8 requires `[GenerateSerializer]`. |
| Static `CaseRequestContext` | Wrong. Should be explicit DI or `AsyncLocal<T>`. |
| `IBehaviorHost` with 14 members | Too wide. Should be split by concern. |
| Generic `BaseBehavior<T>` | Over-engineered. Type parameter doesn't add value. |

---

## What Is Missing

| Feature | Status |
|---|---|
| REST API | Not started — silo serves only `GET /` → `"Hello World"` |
| Production storage | Not implemented — throws `NotImplementedException` |
| Authentication / authorization | Not started |
| Multi-tenancy middleware | Not started (only implicit via grain keys) |
| CaseFileItem grain | Not implemented — model and events exist, grain does not |
| Planning table (discretionary items) | Partially implemented |
| Error recovery / compensation | Not implemented |
| Observability (metrics, tracing) | Logging only (Serilog) |
| Docker / containerization | Not started |

## Notable Dependencies to Replace

| Current | Replace With | Notes |
|---|---|---|
| `Newtonsoft.Json` | `System.Text.Json` | Used in expression evaluation (`Executable.cs`, `JObjectInstance`, `JObjectPropertyDescriptor`, `JTokenExtensions`) and model attributes (`Spec.CMMN.MODEL.cs`). The Jint 3.x upgrade aligns naturally — Jint 3.x has built-in `System.Text.Json` support, replacing the hand-rolled `JObjectInstance`/`JObjectPropertyDescriptor` bridge. |
| `Quartz.NET` | Orleans Reminders | Quartz handles actual timer scheduling; Orleans Reminders currently only provide keepalive. Replace with proper Reminder-based scheduling using the existing `Iso8601` class for duration parsing. Eliminates Quartz entirely. |

---

## Test Coverage Assessment

**Unit tests (42 files, ~6,900 lines):** Strong coverage of behavior logic, state machines, criterion evaluation, and rule evaluation. Tests are well-structured and follow a clear naming convention (`Method__Given_...When_...Then_...`).

**Integration tests (8 files, ~945 lines):** Test grain activation and grain-to-grain interactions using Orleans `TestingHost`. Cover the main grain types.

**Gaps:**
- No API tests (no API to test)
- `MockPlanItemStateMachine` delegates to the real state machine — defeats the purpose of mocking for true unit isolation
- Some tests over-specify call counts (`Times.Exactly(2)`) which couples tests to implementation details
