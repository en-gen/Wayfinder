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

The spec states that intent as a **SHOULD**, not a MUST (§8.6.1, in the sentence immediately following Table 8.12). Satisfying the criteria therefore *permits* a Stage to complete; it does not compel it. That distinction is load-bearing wherever the engine legitimately holds a completion back — see §8's #198 note. The only hard prohibition in this area is RequiredRule's (§8.6.3): a parent **MUST NOT** transition to Complete while a required child sits outside {Completed, Terminated, Failed, Disabled}.

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

**Creation is part of the transition, not a consequence of it.** Table 8.8's `complete` and `terminate` rows both require the RepetitionRule to be re-evaluated *within that transition*, and state that a new instance is created when it evaluates true. In the spec's model there is consequently **no interval** in which a successor has been determined but does not yet exist — which is why the engine has to manufacture one (§8's implementation note) and then defend against it. Read that way, #198's gate is not an invented rule reconciling two tables; it restores an atomicity the spec simply assumes.

One qualification cuts the other way and is worth recording: §8.6.4 phrases the no-entry-criteria case as instances *trying* to create a successor. That is the only textual basis for an engine legitimately refusing a repetition at all, as ours does at the runaway ceiling.

The spec's Example 1 (§8.6.4) is worth reading directly: it walks a repeatable Task B feeding a non-repeatable Task A, and shows three B instances yielding a single A. It is the clearest statement that repetition multiplies instances without multiplying dependents.

> **Implementation note.** The spec describes the trigger and the resulting instance as a single step. In a distributed engine they are separated by a message hop, which is where several of our defects live — a spawned instance can miss the very satisfaction that created it. See [#177](https://github.com/en-gen/Wayfinder/issues/177), [#181](https://github.com/en-gen/Wayfinder/issues/181) (**child-subscription race fixed** — see the residual-window paragraph below; #181's second, separate race on entry-criteria arming is still open).
>
> That same split has a second consequence, only connected to it once the #178 investigation traced a stage that completed over what should have been a live child: the OWNING Stage's own Table 8.12 completion check (§5) runs on its own event stream, independent of the repetition trigger's stream. A "repetition detected" event and the completion check that Table 8.12 mandates on every child transition are not ordered against each other, so the check could run — and the Stage complete — in the gap between "repetition detected" (`Repeated` raised) and "instance created". The result was exactly §4's impossible cell, produced silently. See [#198](https://github.com/en-gen/Wayfinder/issues/198).
>
> **Fixed (#198), and the shape of the fix matters more than the fix.** The two streams are still unordered; nothing tries to order them. Instead the completion check consults the repeating child's own **live state**: a child that is terminal AND carries `Repeated` is one that has determined a successor, and its container refuses to complete until it has durably recorded either spawning that successor or definitively refusing it. The blocking signal is durable state read by direct call, so it cannot be lost or reordered by transport; the clearing signal is written by the container itself, so it cannot race the blocking one. This also covers the case a stream-correlation fix cannot reach at all — the completion check being driven by a *sibling's* transition, which carries no information about the repetition.
>
> This rests on one ordering fact worth stating plainly, because reordering the engine could break it silently: the repetition determination is raised and confirmed inside the same non-reentrant grain turn as the child's own transition into Complete/Terminate, so no observer can see the child as terminal before it has confirmed `Repeated`. Moving that re-evaluation out of the transition turn would reopen #198.
>
> Holding the completion is only half of it, and the other half is easy to miss: **resolving the request has to re-open the check that was held.** Table 8.12 is otherwise evaluated only when a child transitions, and resolving a repetition request produces no transition Table 8.12 reacts to — a successor spawned with a TRUE `manualActivationRule` (§6, the spec's absence default) arrives `Enabled` and never moves again on its own. A container that was held at the exact moment its criteria were otherwise satisfied would therefore stay `Active` forever, having already spent its one chance to notice. Every path that spawns or refuses a request now re-runs the same single completion evaluation.
>
> Residual window, stated rather than hand-waved: a repetition request published but never *delivered* leaves the child blocking completion. A handler-level failure inside a live cluster self-heals — the pulling agent retries and redelivers, which is why the #161 redelivery guard exists. Loss of the queued message itself does not, under the provider this engine is configured with today: `Wayfinder.Silo` uses in-memory streams over an in-memory PubSub store while grain journals are durable, so a silo restart can lose an undelivered repetition request that the child's own journal still records. A durable stream provider would close that gap; nothing in the completion check can.
>
> **One correction to how that window was first written up, because it was recorded as theoretical and was not.** The dominant cause of a lost repetition request had nothing to do with silo restarts or provider durability: `StageBehavior.CreateChild` triggered a new child *before* subscribing to its streams, so a non-blocking child — one that auto-cascades to terminal inside its own `Trigger(Create)` turn — published its repetition-0 request to a stream the parent had not subscribed to yet. **The window closes at delivery, not at the publish** — worth stating precisely, because the looser "resolved at publish time" phrasing describes `SimpleMessageStreams`, and this engine is configured with `AddMemoryStreams("Default")` (`Wayfinder.Silo/Program.cs`), a *persistent* provider: the message is enqueued, a pulling agent polls the queue, and a subscriber's cursor is set when that agent learns of the subscription. An agent that finds no subscriber for a queued message drops it, with no redelivery, and a subscription created afterwards starts at the current cache position and never sees it. Two observations fix the model as delivery-time rather than publish-time: shrinking the agent's poll period makes the defect *worse* (#154), which is meaningless if the outcome were decided at the publish; and #181's raw-stream diagnostic recorded a subscribe at 20ms losing while 0/1/5ms and 50ms won, which a publish-time rule cannot produce. No warning, no error, no redelivery. Since #198 the container then holds its completion indefinitely, which is how it was finally caught: measured as permanent zero progress, not slowness. See [#181](https://github.com/en-gen/Wayfinder/issues/181) and [#153](https://github.com/en-gen/Wayfinder/issues/153).
>
> The fix is ordering, not durability: the subscriptions are now armed before the child is defined or triggered, so nothing published inside the creation turn can arrive at an unsubscribed stream. **That closes the cause, not the class.** The streams are still in-memory and still non-durable, so the paragraph above stands exactly as written for the remaining cases — a silo restart can still lose an undelivered request, and only a durable stream provider closes that. What changed is that the loss is no longer reachable from ordinary, unstressed operation of a perfectly healthy cluster.
>
> The failure mode is nonetheless strictly better than the defect it replaces. Before, the same lost message meant the repetition silently never happened *and* the Stage completed anyway — invisible data loss. Now it is a loud, logged refusal that names the blocking child instances, and only `complete` is gated (`terminate`/`exit` are not), so a Case worker can still resolve the case.
>
> **On an operator override, the spec does weigh in, and the answer is no.** CMMN names an administrator for exactly two things: `re-activate` (Table 8.6, and Table 8.8's Failed → Active row) and `close` (Table 8.6). It defines no operation letting any actor bypass completion criteria. The sanctioned escape from an instance that will not complete is `terminate` — explicitly a Case worker decision, available from Active — whereas `close` is reachable only from a state that is already terminal, so it is not an escape from a stuck Active instance at all. Leaving `terminate`/`exit` ungated is therefore the conformant escape hatch, and it is already what this engine does. A force-complete would invent a transition CMMN does not have *and* reproduce §4's impossible cell. Not implemented, and now deliberately so rather than merely undecided.

## 9. Where the spec pulls both ways: a repetition created and immediately terminated

Sections 4, 5 and 8 are each individually clear, and a model can be authored that puts them in direct conflict. Four conditions together:

1. the container has `autoComplete="true"`;
2. the repeating child is **not** required;
3. the successor lands non-Active — `Enabled` via a true ManualActivationRule (§7's absence default), or `Available` on an unsatisfied entry criterion;
4. all required siblings are already terminal.

Then: the child completes and determines a successor; #198's gate holds the container until that successor exists; the successor spawns `Enabled`; the gate releases; Table 8.12's TRUE column is satisfied, because `Enabled` is not `Active` and every required child is terminal; the container completes; and §4's cascade drives the successor to `Terminated`. **The repetition is born and dies without ever running.**

Each rule is applied correctly, and the spec supports both halves — Table 8.12 *permits* the completion, Table 8.7 and Table 8.9 *require* the cascade once it happens — while §8.6.4's purpose is nonetheless defeated. CMMN offers no way to express "repeat, and the Stage is not done until the repetition has been dealt with" other than by changing one of the four conditions. The engine therefore has no principled basis for preferring either reading, and picking one at runtime would be inventing semantics the spec declines to state.

The consequence is bounded: the terminated instance is journaled, so this is auditable rather than silent loss. It is an authoring-intent mismatch, which is why the remedy proposed is a deploy-time model-validation **warning** naming the three available fixes (set `autoComplete="false"`, make the child required, or accept it) rather than any change to execution. See [#225](https://github.com/en-gen/Wayfinder/issues/225).

---

## Conformance status

Rules above that Wayfinder is known to violate today, each with a reproducing test:

| Rule | Issue | Status |
|---|---|---|
| Cross-stage OnParts (§1) | [#176](https://github.com/en-gen/Wayfinder/issues/176) | Confirmed, unfixed — fix needs a scope-matching design |
| Repetition instance activation (§8) | [#177](https://github.com/en-gen/Wayfinder/issues/177) | Confirmed, unfixed |
| Terminated stages spawning children (§3) | [#178](https://github.com/en-gen/Wayfinder/issues/178) | **Fixed** — terminal containers refuse a late repetition spawn; Suspended ones buffer it and replay on resume, since suspension preserves rather than discards (§2) |
| Completion over live children (§4) | [#179](https://github.com/en-gen/Wayfinder/issues/179) | **Fixed** — a completing Stage now drives non-terminal Stage and Task children to Terminated. Milestones and EventListeners deliberately survive: Table 8.9's `complete` rows keep them Available/Suspended |
| Timer repetition ignoring the rule (§8) | [#182](https://github.com/en-gen/Wayfinder/issues/182) | Confirmed, unfixed |
| Stage completion racing an in-flight repetition (§5) | [#198](https://github.com/en-gen/Wayfinder/issues/198) | **Fixed** — the completion check now blocks on the repeating child's live state rather than trying to order the two streams (see §8's implementation note) |
| Repetition request published before the parent subscribes (§8) | [#181](https://github.com/en-gen/Wayfinder/issues/181) | **Fixed** — `StageBehavior.CreateChild` arms both child subscriptions before defining or triggering the child, so a non-blocking child's creation-turn publishes can no longer reach a stream with zero subscribers. Streams remain non-durable; this closes the dominant cause of loss, not the possibility. #181 stays open for its second race: `TaskBehavior`/`MilestoneBehavior` arm their own entry criteria *inside* their `Trigger(Create)` turn, so a concurrent fan-out can still drop a sibling's sentry satisfaction. Not a mechanical hoist — §8.5 evaluates entry criteria in the **Available** state, so arming earlier needs design |
| Repetition created then immediately terminated (§9) | [#225](https://github.com/en-gen/Wayfinder/issues/225) | Not a violation — the spec permits both readings. Proposed remedy is a deploy-time authoring warning, not an execution change |

When you fix one, update this table — it is meant to stay honest about where the engine and the spec disagree.
