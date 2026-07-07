# Case.Flow Product Roadmap

*Owner: PM/PO (drafted 2026-07-06). Companion to the technical deep-dive in [06-commercial-readiness-deep-dive.md](06-commercial-readiness-deep-dive.md). Tracker scaffolding for Azure DevOps (`brute-force/Case`) lives in [/scripts](../scripts).*

---

## 1. Product Vision

**Case.Flow: the cloud-native, distributed case-management engine for .NET** — a CMMN 1.1 runtime on Microsoft Orleans with a first-class API, a web modeler (cmmn-js lineage), a task/operations UI, and an event-sourced audit trail — packaged so it can be **licensed to ISVs and platform teams, embedded in vertical SaaS, or sold as an asset**.

Positioning notes (evidence in doc 06 §6):
- Lead with **"case management / dynamic workflow for .NET"**; CMMN compliance is the credibility layer, not the headline — the standard is niche (frozen 1.1; Flowable is the lone active champion) and .NET has **zero** living alternatives, which is precisely the licensing opportunity.
- The strongest demo narrative is a vertical showcase (audit engagement with risk-based tailoring — the PPC-shaped mechanism our planning tables already implement) sitting on a horizontal engine.
- Sale/licensing value accrues from: demonstrable spec fidelity (conformance suite), a benchmark whitepaper (Orleans scale story), clean IP, docs/SDK quality, and a working modeler→run→inspect loop. Every milestone below feeds at least one of those.

## 2. Current Baseline (measured, 2026-07-06)

- Engine control plane ~80–85% spec-adherent; data plane ~15% (deviation registry D1–D10, blockers B1–B6 in doc 06).
- **209/209 tests green** (on modern runtime via `DOTNET_ROLL_FORWARD`).
- **Coverage (first-ever measurement):** integration run — Flow.Grains 66% line / 24% branch; unit run — Flow.Grains 27% line / 46% branch; Interfaces 4% line (unit); **Flow.Silo 0% (never loaded by any test)**; runs not merged.
- **CI/CD: rebuilt 2026-07-06.** A classic designer pipeline ("Case-Flow-CI", last build 2020-09-13, watching `master`) was found server-side, renamed "(legacy, disabled)" and disabled. Its replacement **`Case.Flow.CI`** (ADO definition id 4, YAML) targets `azure-pipelines.yml` on `develop` and activates as soon as that file merges.
- Hosted at `dev.azure.com/brute-force/Case/_git/Case.Flow` (repo renamed from Case-Flow 2026-07-07); no GitHub presence, so "issues/milestones" = ADO Epics/Features (scaffold script provided).

## 3. Milestones

Sizing assumes 2–3 engineers; ranges are dev-weeks of focused work. Milestones overlap deliberately (M3 UI spikes can start during M2).

### M0 — Solid Ground (4–6 wks) · *"trustworthy foundation"*
Modernize + make truth continuously visible.
1. **CI/CD live** — commit the provided `azure-pipelines.yml`; branch policy on `develop` requiring green build + tests; publish merged coverage. *(AC: PR cannot merge red; coverage visible per build.)*
   Includes **GitVersion semantic versioning**: `GitVersion.yml` at repo root (GitFlow increments; tag-prefix `Case.Flow-`; **baseline 1.0.0** via `next-version`, so every pre-GA build is a preview of 1.0.0 — `1.0.0-develop.<n>`, `1.0.0-rc.<n>`), a Version job in `Case.Flow.CI` that republishes outputs for downstream jobs and names each run accordingly, assemblies stamped via `/p:Version`/`AssemblySemVer`/`InformationalVersion`. *(Tag `Case.Flow-1.0.0` on mainline at GA; tags become the version source of truth thereafter.)*
2. **.NET 8 + Orleans 8.x migration** — per doc 03: `[GenerateSerializer]` sweep (~45 `[Serializable]` types), Host/TestCluster builders, Jint 3.x, System.Text.Json, drop `OrleansAzureUtils` 2.4.5. *(AC: 209 tests green natively, no roll-forward.)*
3. **Dependency & CVE remediation** — Newtonsoft ≥13.0.1, AutoMapper patch, transitive criticals; `dotnet list package --vulnerable` clean gate in CI.
4. **Systemic bug fixes** — `Task.Factory.StartNew(async…)` ×4, `BaseEvent.Occurred` replay timestamps, `HashSet<OnPart>` identity, sentry double-satisfy guard. *(AC: regression test per fix.)*
5. **Docs corrections** — apply doc 06 §5 list; README claims match reality.

### M1 — True Engine (8–10 wks) · *"the spec's data plane, done"*
The gap between "state-machine demo" and "engine you can license."
1. **CaseFile/CaseFileItem grains** — §8.3 lifecycle + operations, publishing `CaseFileItemTransitionedEvent` (unblocks case-file sentries, timer start-triggers, D2).
2. **Expression context binder** — typed dot-path case-file access injected into evaluation (fixes D1); shared by all expression engines (see M-EXPR).
3. **Sentry completion** — ifPart-only evaluation (D3), per-repetition reset (D5), stage exit-criteria subscription (D6), `sentryRef` mode (D10).
4. **Rules completion** — Table 8.12 autoComplete fix (D4), repetition first-eval discard + repeat-on-complete (D7), CasePlanModel close/lockdown behavior (D8).
5. **Structural `.cmmn` import/export + capability lint** — `XmlSerializer` path validated by golden-file round-trips against Trisotech/Flowable samples; lint rejects/warns on constructs the runtime can't honor yet (doc 06 §2.4).
6. **Conformance scenario suite** — generate state-transition tests from spec Tables 8.5–8.11 (our own TCK; no official one exists — this becomes a marketable asset).
7. ⚖️ **Decision gate: licensing model** — source-available + commercial embedded (Camunda-8-style) vs OSS-core + enterprise (Flowable-style) vs closed. Decide before M2 API surface freezes.

### M-EXPR — Expression Languages (epic spanning M1→M3; ~4–6 wks engine-side)
See §5 for the full analysis. Deliverables:
1. **Pluggable `IExpressionEngine`** keyed by the model's `Expression.Language` URI, with per-engine sandbox budgets; compilation cache. Jint hardened (timeout, MaxStatements, memory) ships first.
2. **FEEL spike → GA** — evaluate `feelin` (bpmn-io's JS FEEL) hosted on our existing Jint sandbox vs a native .NET FEEL port; FEEL becomes the *flagship authored language* (modeler autocomplete in M3; DMN synergy for future DecisionTask).
3. **XPath compatibility mode** — spec-default language for imported conformant models (System.Xml.XPath over a case-file XML projection). Gated on conformance marketing value.
4. **JUEL import shim (best-effort)** — translate the common `${…}` subset at import time with per-expression warnings; explicitly **not** a runtime engine (JUEL is Java EL; no credible .NET runtime exists — full support would be an unbounded port for near-zero demand).

### M2 — Cloud Native (8–10 wks) · *"runs somewhere real"*
1. **Deployed Orleans** — Azure clustering + durable grain/journal storage; replace fire-and-forget SMS streams for criticality-1 events (durable provider or direct signaling); snapshotting + event-schema versioning.
2. **Durable timers** — Orleans Reminders (or clustered AdoJobStore Quartz); fix singleton scheduler keying (B3). *(AC: chaos test — silo kill mid-case loses no timers/events.)*
3. **API layer** — REST (+ optional gRPC) with OpenAPI: definitions CRUD/versioning, case lifecycle, task inbox ops, planning ops, case-file ops, event/history queries; `Flow.Client` SDK on NuGet.
4. **AuthN/Z + tenancy** — OIDC, per-call tenant enforcement filter (kills ambient-context trust, B5), role→user resolution.
5. **Observability & packaging** — OpenTelemetry traces/metrics, health checks; container images + Helm chart/Bicep reference deployment (AKS/Container Apps). Delivery pipeline lands here as **`Case.Flow.CD`** (name reserved; companion to `Case.Flow.CI`).
6. **Benchmark report** — 10k concurrent cases × 50 plan items; publishable numbers (sales asset).

### M3 — Faces (10–14 wks) · *"model → deploy → run → inspect, visually"*
1. ⚠️ **Modeler diligence spike (do first)** — `cmmn-js` is effectively unmaintained by bpmn.io and carries the bpmn.io license (watermark requirement) — decide: fork-and-own cmmn-js, rebuild on `diagram-js`, or negotiate a commercial license. Output: decision memo + legal check. *(This is the single biggest UI risk; budget 1–2 wks.)*
2. **Web modeler MVP** — author/validate CMMN diagrams, deploy to engine via API (round-trips our §9 import/export); expression autocomplete (FEEL-first).
3. **Tasklist app** — human-task inbox: claim/complete, planning-table item selection (the discretionary-items UX), simple forms (evaluate `form-js`).
4. **Case inspector ("cockpit")** — live plan-item state visualization on the diagram, event-sourced timeline (our differentiator: full audit trail replay), sentry/criteria status.
5. **Admin console** — tenants, definition versions, migrations.

### M4 — Commercialization (6–8 wks + ongoing) · *"someone can buy this"*
1. Licensing implementation per M1 gate (repo split/license keys/notices; bpmn.io compliance).
2. Docs site + quickstarts (model-to-running-case in 15 minutes); samples gallery — insurance claim, employee onboarding, **audit engagement showcase** (risk-tailored program via planning tables; Crunchafi data-extraction feed as the AI-analytics demo).
3. Whitepapers: conformance suite results + benchmark.
4. Design-partner program (2–3 teams; target .NET ISVs needing case semantics + one audit/accounting firm via Crunchafi network).
5. Pricing/packaging (embedded per-app license, per-core self-host, managed SaaS tier) + **sale-readiness data room**: IP audit (XmlSchemaClassGenerator MIT, OMG spec usage, bpmn.io license posture, no PPC content), architecture dossier (docs 01–07), conformance + benchmark evidence.

**Cumulative: ~36–48 dev-weeks; demoable end-to-end ≈ end of M3 (~2–3 quarters with 2–3 devs); licensable GA ≈ 3–4 quarters.**

## 4. Test Coverage Plan (answers "do we need more?")

Yes — but targeted, not blanket. Today's measured reality: strong scenario tests exist (209 green) but **branch coverage of the engine is 24–46% depending on suite, the two suites aren't merged, and Flow.Silo has literally zero coverage**. Priorities:

1. **Merge + baseline in CI (M0)** — the pipeline provided merges unit+integration coverage; ratchet the gate (fail on decrease) rather than picking an arbitrary %.
2. **Spec-table property tests (M1)** — generate transition-matrix tests from Tables 8.5–8.11; cheap, exhaustive, and doubles as the conformance suite.
3. **Scenario gaps** — deep-hierarchy suspend/resume cascades, full repetition cycles, `autoComplete=false` + discretionary items, sentry resume-after-deactivation, duplicate-event idempotency, multi-tenant isolation (zero tests today).
4. **Every D1–D10 fix lands with a pinning test.**
5. Targets by M1 exit: Flow.Grains ≥80% line / ≥60% branch; Silo covered by API-level tests in M2. Interfaces DTOs excluded from targets (noise).

## 5. Expression Language Strategy (FEEL / JUEL / XPath / JS)

**Where we are:** everything routes to Jint (JavaScript) regardless of the model's `language` attribute, with an empty evaluation context (D1) and no sandbox limits (B4).

**Why it matters here:** (a) modeler interop — Trisotech emits FEEL, Flowable-authored models carry JUEL `${…}`, the spec default is XPath; (b) authoring UX in our own modeler — analysts write `riskScore > 3 and industry = "banking"`, not JS; (c) multi-tenant safety — expression engines are tenant-supplied code; (d) DMN — any future DecisionTask needs FEEL anyway, so FEEL investment double-dips.

**Decisions proposed:**

| Language | Role | Rationale |
|---|---|---|
| **FEEL** | **Flagship authored language** (modeler default) | Business-readable, side-effect-free by design (ideal sandbox), DMN-aligned. Implementation path: spike `feelin` (bpmn-io's JS FEEL engine) hosted on our existing Jint sandbox — lowest effort, one sandbox story; graduate to a native .NET FEEL implementation if perf/fidelity demands. |
| **JavaScript (Jint)** | Power-user escape hatch; kept | Already works; must gain timeout/statement/memory budgets (B4) before any multi-tenant exposure. |
| **XPath** | Conformance/import compatibility mode | Spec default; `System.Xml.XPath` over an XML projection of the case file is tractable. Gated on whether we market conformance (M1 decision gate). |
| **JUEL** | **Import-translation shim only** | JUEL is *Java* EL — no credible .NET runtime, and demand exists only via Flowable-model imports. Translate the common subset at import with warnings; refuse silently-wrong execution. Committing to full JUEL would be an unbounded port serving a migration niche — not on the critical path. |

**Architecture:** `IExpressionEngine` registry keyed by language URI; shared typed case-file context binder (the D1 fix) feeds every engine; compile-once caching; per-engine budgets. The engine work rides in M1; modeler autocomplete/validation rides in M3.

## 6. Risk Register (top 5)

| Risk | Exposure | Mitigation |
|---|---|---|
| CMMN demand is thin; "standards engine" markets poorly | Revenue | Lead with case-management-for-.NET + vertical showcase; conformance as credibility, not headline |
| cmmn-js is unmaintained + bpmn.io watermark license | M3 schedule/legal | Diligence spike first story of M3; fork-and-own or diagram-js rebuild fallback |
| Orleans expertise concentration | Delivery | Architecture dossier (done: docs 01–06), pairing, conformance suite as safety net |
| Fire-and-forget event loss reaches a customer before M2 | Reputation | No external pilots before M2 chaos-test exit gate |
| Effort creep vs. 2–3 dev capacity | Schedule | Milestone exit gates are demos, not documents; cut XPath/gRPC/marketplace before cutting M1 data plane |

## 7. Tracker Mapping

Milestones M0–M4 + M-EXPR = **Epics**; numbered items = **Features** (with acceptance criteria in descriptions). Scaffold into `brute-force/Case` via [scripts/scaffold-roadmap.ps1](../scripts/scaffold-roadmap.ps1) (requires a PAT with *Work Items Read & Write*; the credential in the git remote lacks REST scope). The script is idempotent by title and tags everything `roadmap-v1`.
