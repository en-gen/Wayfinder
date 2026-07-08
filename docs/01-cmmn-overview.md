# CMMN Overview

## What Is CMMN?

Case Management Model and Notation (CMMN) is an [OMG](https://www.omg.org/) standard for modeling knowledge-intensive, unpredictable work — situations where the sequence of activities cannot be fully predetermined upfront. Unlike BPMN (which models fixed process flows), CMMN models *cases* where a human worker makes decisions about what to do next based on what they discover.

A **case** is a work item driven by data, events, and human decisions rather than a fixed sequence of steps.

**Canonical examples:** legal matters, patient care plans, insurance claims, audit engagements, HR investigations, customer support escalations.

## CMMN vs BPMN

| BPMN | CMMN |
|---|---|
| Flow-driven (sequence arrows) | Data/event-driven (no flow arrows) |
| Predictable, repeatable processes | Ad hoc, knowledge-intensive cases |
| "Do A, then B, then C" | "These things *may* happen, in any order" |
| Worker follows the system | System supports the worker |

CMMN is part of the OMG "decision triple" alongside BPMN and DMN:
- **BPMN** — how processes flow
- **CMMN** — how cases are managed
- **DMN** — how decisions are made

Cases can invoke BPMN processes and DMN decisions as tasks within the case plan.

---

## Core Concepts

### Case Plan Model

The top-level container for a case instance. Defines all the work that *may* happen within the case. Has its own lifecycle: Active → Completed / Terminated / Failed.

### Plan Items

Work units within a case. Each plan item has a **definition** (what it is) and an **instance** (its runtime state within a specific case).

| Plan Item Type | Description |
|---|---|
| **Stage** | A container grouping related work. Can be nested. |
| **HumanTask** | Work performed by a person. |
| **ProcessTask** | Delegates to an external BPMN process. |
| **CaseTask** | Invokes a sub-case. |
| **DecisionTask** | Invokes a DMN decision. |
| **Milestone** | Marks that a certain state has been achieved (not work — just a marker). |
| **TimerEventListener** | Reacts to a timer firing. |
| **UserEventListener** | Reacts to a manual user event. |

### Plan Item Lifecycle

Every plan item (except milestones and event listeners) follows this state machine:

```
Uninitialized
     ↓ Create
  Available
     ↓ Enable (if ManualActivationRule = true)    or    ↓ Start (if ManualActivationRule = false)
  Enabled ──────────────────────────────────────────→ Active
     ↓ Disable                                          ↓
  Disabled                              Suspend / Resume / Complete / Terminate / Fault
```

Milestones and event listeners have a simplified lifecycle: Available → Completed / Terminated.

### Sentries

Sentries are the mechanism by which plan items activate or complete. A sentry defines *when* a transition becomes permissible.

A sentry consists of:
- **OnPart** — what event must occur (another plan item transitioning, or a case file item changing)
- **IfPart** — a boolean condition (guard) that must also evaluate to true

Both must be satisfied for the sentry to fire.

**Entry criterion** — a sentry attached to a plan item that permits it to become Enabled or Active.
**Exit criterion** — a sentry attached to a plan item or stage that permits it to Complete or Terminate.

### Case File

The data container for the case. Case file items hold documents, data objects, or values that the case works with. Sentries can react to case file item state changes (e.g., "when a document is uploaded, activate this task").

### Discretionary vs Required Items

| Type | Visual | Meaning |
|---|---|---|
| **Required** | Solid border | Must complete for the enclosing stage to complete |
| **Discretionary** | Dashed border | Worker *may* choose to perform — not mandatory |

Discretionary items are managed via the **Planning Table** — a worker can add them to the case if needed.

### Rules

Rules are boolean expressions evaluated at runtime to control plan item behavior:

| Rule | Effect |
|---|---|
| **ManualActivationRule** | If true, worker must explicitly start the task (not auto-activated) |
| **RequiredRule** | If true, this item must complete before the stage can complete |
| **RepetitionRule** | If true, the item can be performed multiple times |
| **ApplicabilityRule** | Controls whether a discretionary item can be added to the planning table |

---

## How Case.Flow Implements CMMN

### Virtual Actor Per Element

Each CMMN element at runtime is an Orleans grain (virtual actor):

```
CaseGrain               — the case instance (CasePlanModel behavior)
  └─ PlanItemGrain      — each stage, task, milestone, event listener
       └─ SentryGrain   — each entry/exit criterion
```

Grains are keyed by `(TenantId, Address)` where address encodes the element's position in the case hierarchy (e.g., `caseId.stageId.taskId`).

### Event Sourcing

All state changes are captured as immutable events applied to grain state via Orleans `JournaledGrain<TState>`. State is rebuilt by replaying the event log. This provides:
- Full audit trail of every case transition
- Point-in-time state reconstruction
- Durable, replayable history

### Behavior Pattern

Grain logic is separated from Orleans concerns via a behavior pattern:
- **CaseGrain / PlanItemGrain** — handle Orleans lifecycle (activation, streams, event sourcing)
- **IBehaviorHost** — adapter interface the grain presents to behaviors
- **StageBehavior, TaskBehavior, MilestoneBehavior, etc.** — pure domain logic, testable without Orleans

### Expression Evaluation

Rules and sentry if-parts are JavaScript expressions evaluated by `ExpressionGrain` (a stateless Orleans worker grain) using the [Jint](https://github.com/sebastienros/jint) JavaScript engine. This allows runtime-configurable conditions without code changes.

### Timer Events

Timer event listeners use [Quartz.NET](https://www.quartz-scheduler.net/) for scheduling, with ISO 8601 duration/interval parsing. Each case has a `TimerEventSchedulerGrain` that manages its timers.

---

## CMMN in Financial Audit

A financial audit engagement is a strong fit for CMMN modeling. Key mapping:

| Audit Concept | CMMN Concept |
|---|---|
| Audit engagement | Case |
| Planning, fieldwork, reporting phases | Stages |
| Control test, substantive procedure | HumanTask |
| Lease accounting analysis (Lease Accounting) | ProcessTask |
| ERP data extraction (Data Extraction) | ProcessTask |
| "Risk assessment complete" | Milestone |
| Extended procedures triggered by a finding | Discretionary task (entry criterion = finding recorded) |
| Mandatory ISA/GAAS procedures | Required tasks |
| Partner sign-off required | ManualActivationRule |
| Re-test after failure | RepetitionRule |

See [docs/04-market-analysis.md](docs/04-market-analysis.md) for the full market context.
