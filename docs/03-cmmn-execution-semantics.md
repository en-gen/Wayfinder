# CMMN Execution Semantics — Implementer's Reference

A working reference for the normative rules Wayfinder implements, drawn from **OMG CMMN 1.1** (`formal-16-12-01`), clause 8 *Execution Semantics*.

This is a **paraphrase with citations**, not a copy. Every rule below is restated in our own words and organized around the questions that actually come up while building the engine. Section and table numbers are given so anyone holding the spec can verify a claim in seconds.

> **The spec is not in this repo, and must not be.** It is OMG-copyrighted and this repository is public. Download it from [omg.org/spec/CMMN](https://www.omg.org/spec/CMMN/) and keep a local copy. Reading it to inform implementation is fine; redistributing it is not.
>
> **Navigating a local PDF:** printed page ≈ PDF page − 16 (the front matter runs ~16 pages). Clause 8 begins at printed page 107.

## Where to look

| Topic | Clause / Table | Printed page |
|---|---|---|
| CaseFileItem lifecycle | §8.3 | 107 |
| Case instance lifecycle | §8.4.1, Tables 8.5–8.6 | 111 |
| **Stage/Task lifecycle** | §8.4.2, Figure 8.3 | 113 |
| Stage/Task states | Table 8.7 | 114 |
| Stage/Task transitions | Table 8.8 | 115 |
| **Top-down state propagation** | **Table 8.9** | **117** |
| EventListener / Milestone lifecycle | §8.4.3, Tables 8.10–8.11 | 119 |
| Sentry satisfaction | §8.5 | 121 |
| Behavior property rules | §8.6 | 121 |
| Stage completion criteria | Table 8.12 | 122 |
| RepetitionRule | §8.6.4 | 122 |

---

## 1. Becoming Available — including across stages

A Stage or Task instance becomes Available in one of two ways (Table 8.7):

1. its containing Stage instance moves to Active; **or**
2. it has a Sentry whose OnPart `sourceRef` points at an item **outside its enclosing Stage**, and that Sentry is satisfied.

Case 2 is **bottom-up activation**: the containing Stage — and recursively every Stage up to the enclosing Stage of the referenced item — moves to Active if not already. A missing entry criterion counts as satisfied.

> **Implementation note.** Cross-stage OnParts are mandatory behavior, not an exotic edge case. A scope filter that only accepts sources at-or-below the sentry's own position violates this rule. See [#176](https://github.com/en-gen/Wayfinder/issues/176).

## 2. Suspension preserves state; it never discards it

Suspension is a pause with memory (Table 8.9, and the history pseudo-state in Figure 8.3):

- A Stage entering Suspended drives children in Available / Enabled / Disabled / Active to Suspended via `parent suspend`.
- On `parent resume`, **each child returns to the state it held before suspension**.
- Children already in Completed, Terminated, or Failed are unaffected.

Two consequences that matter for the engine:

- `complete` is an **Active → Completed** transition only (Table 8.8), and entry-criterion sentries are evaluated while an item is Available (§8.5). So **no repetition trigger can legitimately originate inside a genuinely Suspended Stage.**
- Therefore any repetition request observed while suspended was *earned before* suspension and is merely late in delivery — an artifact of asynchronous transport, not a modeled scenario. Discarding it loses work the spec says occurred. See [#178](https://github.com/en-gen/Wayfinder/issues/178), [#182](https://github.com/en-gen/Wayfinder/issues/182).

## 3. Termination cascades to everything

A Stage entering Terminated drives **every** non-terminal child (Available, Enabled, Disabled, Active, Suspended, Failed) to Terminated via `exit` (Table 8.9). `exit` also fires when an item's own exit criterion becomes true.

After termination nothing live remains inside the Stage — so a terminated container can have no child left to request a repetition, and spawning into one produces a state the model does not contain.

Note `fault` is the exception: Failed **must not** propagate (Table 8.8).

## 4. A Stage cannot complete over a live child

Table 8.9's `complete` rows mark the combination of a Completed Stage with a child in Available, Enabled, Active, or Suspended as **impossible**. Only Disabled, Failed, Completed, and Terminated children may coexist with a completed parent.

> **Implementation note.** This makes "non-terminal children survive a completing Stage" a conformance violation rather than a missing convenience. See [#179](https://github.com/en-gen/Wayfinder/issues/179).

## 5. Stage completion criteria (Table 8.12)

| `autoComplete` | Completes when |
|---|---|
| **TRUE** | no Active children, **and** every required child (RequiredRule true) is Disabled, Completed, Terminated, or Failed |
| **FALSE** | no Active children, **and** all children are in that same terminal set, **and** no DiscretionaryItems remain — *or* manual completion plus the required-children condition |

The intent: a Stage should complete once the user has no further planning or work available to them.

> Note the TRUE column carries **no** DiscretionaryItems term. A Stage whose only children are discretionary therefore satisfies it vacuously. See [#180](https://github.com/en-gen/Wayfinder/issues/180).

## 6. Sentries (§8.5)

A Sentry is satisfied when any one of these holds:

- all OnParts satisfied **and** the IfPart evaluates true;
- all OnParts satisfied **and** there is no IfPart;
- the IfPart evaluates true **and** there are no OnParts.

An OnPart is satisfied when the Sentry named by its `sentryRef` has occurred, or when its `sourceRef` makes the transition named by its `standardEvent`.

Where multiple entry criteria exist, **one** is enough to move the item out of Available; likewise one exit criterion suffices to terminate. Entry criteria are evaluated while the item is Available; exit criteria while the CasePlanModel, Stage, or Task is Active. A single event may satisfy several sentries. A Sentry with no OnPart must have an IfPart.

## 7. Behavior property rules (§8.6)

| Rule | Evaluated when | Meaning |
|---|---|---|
| **ManualActivationRule** | an entry criterion is satisfied | true → Available becomes Enabled (awaits a human); false → straight to Active |
| **RequiredRule** | on instantiation into Available | true → the parent Stage must not complete until this item is Completed, Terminated, Failed, or Disabled. Absent ⇒ false |
| **RepetitionRule** | on instantiation, and again on each repetition trigger | governs whether a further instance is created |
| **ApplicabilityRule** | planning | governs whether a DiscretionaryItem may be planned |

Note the spec's defaults differ per rule — ManualActivationRule's absent-default is true, RequiredRule's is false. Getting these backwards on an error path is exactly the bug fixed in [#158](https://github.com/en-gen/Wayfinder/issues/158).

## 8. Repetition triggers (§8.6.4, Table 8.8)

- **With entry criteria:** a new instance is created each time an entry criterion carrying an OnPart is satisfied and the RepetitionRule re-evaluates true. The new instance moves from Available to Active or Enabled per the ManualActivationRule.
- **Without entry criteria:** the RepetitionRule is re-evaluated on transition into Complete or Terminate; if true, a new instance is created.
- On first instantiation the RepetitionRule is evaluated and its result **discarded** — the first instance is not a repetition.

The spec's Example 1 (§8.6.4) is worth reading directly: it walks a repeatable Task B feeding a non-repeatable Task A, and shows three B instances yielding a single A. It is the clearest statement that repetition multiplies instances without multiplying dependents.

> **Implementation note.** The spec describes the trigger and the resulting instance as a single step. In a distributed engine they are separated by a message hop, which is where several of our defects live — a spawned instance can miss the very satisfaction that created it. See [#177](https://github.com/en-gen/Wayfinder/issues/177), [#181](https://github.com/en-gen/Wayfinder/issues/181).
>
> That same split had a second consequence, only connected to it once the #178 investigation traced a stage that completed over what should have been a live child: the OWNING Stage's own Table 8.12 completion check (§5) ran on its own event stream, independent of the repetition trigger's stream. A "repetition detected" event and the completion check that Table 8.12 mandates on every child transition were not ordered against each other, so the check could run — and the Stage could legitimately complete — in the gap between "repetition detected" (`Repeated` raised) and "instance created". The result was exactly §4's impossible cell, produced silently: nothing in the completion check knew a repetition was in flight. Fixed by [#198](https://github.com/en-gen/Wayfinder/issues/198): the terminal child now decides its RepetitionRule re-evaluation BEFORE publishing its own transition, and carries the verdict on that SAME event (`PlanItemTransitionedEvent.WillRepeat`) — the completion check defers for as long as any child's verdict is still outstanding, closing the gap at its source instead of trying to order two independent streams.

---

## Conformance status

Rules above that Wayfinder is known to violate today, each with a reproducing test:

| Rule | Issue | Status |
|---|---|---|
| Cross-stage OnParts (§1) | [#176](https://github.com/en-gen/Wayfinder/issues/176) | Confirmed, unfixed — fix needs a scope-matching design |
| Repetition instance activation (§8) | [#177](https://github.com/en-gen/Wayfinder/issues/177) | Confirmed, unfixed |
| Terminated stages spawning children (§3) | [#178](https://github.com/en-gen/Wayfinder/issues/178) | Confirmed, unfixed |
| Completion over live children (§4) | [#179](https://github.com/en-gen/Wayfinder/issues/179) | Confirmed, unfixed |
| Timer repetition ignoring the rule (§8) | [#182](https://github.com/en-gen/Wayfinder/issues/182) | Confirmed, unfixed |
| Stage completion racing an in-flight repetition (§5) | [#198](https://github.com/en-gen/Wayfinder/issues/198) | Fixed — `BaseBehavior.HandleTransitioned` now decides the no-entry-criteria RepetitionRule re-evaluation BEFORE publishing `PlanItemTransitionedEvent` and embeds the verdict (`WillRepeat`); `StageBehavior` defers Table 8.12's completion check while any child's verdict is still outstanding (`StageBehaviorStore.PendingRepetitionSourceInstanceIds`), closing the race instead of ordering the two streams |

When you fix one, update this table — it is meant to stay honest about where the engine and the spec disagree.
