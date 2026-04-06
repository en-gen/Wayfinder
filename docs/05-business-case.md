# Business Case: Case-Flow as a Crunchafi Product

*April 2026. Prepared for internal review.*

---

## The Opportunity in One Sentence

Crunchafi's strategy is to acquire products that serve the financial audit process — Case-Flow is the orchestration layer that turns those acquisitions into a platform rather than a collection of silos.

---

## The Problem We're Solving

Today a CPA firm using Crunchafi's products works like this:

- They log into **Data Extraction** to extract client financials
- They log into **Lease Accounting** to handle lease accounting
- They manually coordinate between the two
- They use **Thomson Reuters Guided Assurance** (or a spreadsheet) to track where they are in the engagement
- Every new product Crunchafi acquires adds another tool to manage

There is no single place where the audit engagement *lives*. There is no system of record for what has been done, by whom, when, and in what order. There is no way to enforce that required procedures were completed before an opinion was signed.

That's the gap Case-Flow fills.

---

## What We Already Have

This is not a greenfield proposal. A CMMN 1.1 engine was built and validated several years ago, recently recovered from version control. It includes:

- A fully implemented, spec-compliant CMMN 1.1 runtime built on Microsoft Orleans
- Complete state machine implementation for all plan item types
- Sentry/criterion evaluation with JavaScript expression support
- Timer event scheduling
- Event sourcing via Orleans JournaledGrain — every state transition is captured immutably
- Multi-tenant grain architecture
- ~8,100 lines of unit and integration tests

The engine works. What it needs is modernisation (Orleans 8, .NET 8), a REST API, production infrastructure, an engagement dashboard, and a task inbox. That is well-understood engineering work, not research.

**Estimated effort: 2–3 months with a team of 2–3 developers, led by the original author in an architect and technical lead capacity.** The estimate is credible because the person who built the engine is guiding the modernisation — architectural decisions, dependency upgrades, and production infrastructure are known quantities, not discoveries.

---

## Strategic Fit

### Crunchafi's stated direction is acquisition

The rebrand from LeaseCrunch to Crunchafi explicitly signals a multi-product platform ambition. Every product acquired to serve the audit process faces the same integration problem: how does it connect to the others? How does a CPA firm run a coordinated engagement across multiple tools?

Case-Flow is the answer to that question. Each acquired product becomes a **ProcessTask** within a CMMN case — invoked at the right point in the engagement, with results flowing into the case file and triggering downstream work.

```
[Audit Engagement Case]
    ├─ ProcessTask: Data Extraction      ← ERP data extraction
    ├─ ProcessTask: Lease Accounting     ← Lease accounting
    ├─ ProcessTask: Cash Flow                      ← Cash flow analysis
    └─ ProcessTask: [Next acquisition]             ← plugs in on day one
```

Without Case-Flow, each acquisition compounds the coordination problem. With it, each acquisition compounds the platform's value.

### The Thomson Reuters Dependency Risk

The TR partnership has been a meaningful growth driver — Lease Accounting is embedded in Guided Assurance and TR's sales force carries it. But the partnership reveals a structural risk that compounds over time:

**We do not control the relationship.** TR chose Validis — not Data Extraction — as their data extraction partner for Audit Intelligence. Both products compete in the same space. TR made a choice that excluded us from a major workflow integration. They can make that choice again, for any product, at any time.

**TR controls the engagement workflow.** When a CPA firm runs an audit in Guided Assurance, the engagement lives in TR's platform. Lease Accounting is a line item in that workflow — useful, but substitutable if TR builds the capability internally or partners with a competitor. The firm's loyalty is to TR's platform, not to Crunchafi.

**The incentive structure is asymmetric.** TR benefits from Crunchafi's products making their platform more complete. Crunchafi benefits from TR's distribution. But TR can replace Crunchafi's products; Crunchafi cannot replace TR's distribution without building something of its own.

Case-Flow is that something. It does not require ending the TR partnership — in the near term, Lease Accounting continues as a TR partner regardless of whether Case-Flow exists. But it means Crunchafi is building towards a position where TR's decisions about partnerships are no longer existential.

### Firms Should Own Their Workflow — Not Thomson Reuters

This is the most important differentiator in the pitch.

Thomson Reuters' Guided Assurance embeds PPC methodology. Firms using it run TR's audit program — TR's structure, TR's sequence, TR's procedures. For many firms this is fine. For a significant number it is not:

- **Large regionals and specialist firms** have built proprietary methodologies over decades. PPC is not their process.
- **International firms** operate under different standards where PPC is not relevant.
- **Any firm that has been through a quality review** knows that their methodology needs to be theirs to defend.

Case-Flow's answer is straightforward: **the firm defines the workflow.** Using the CMMN modeller, a firm encodes their own audit program — their required procedures, their risk thresholds, their evidence standards, their sign-off requirements. That program runs consistently across every engagement. It is stored as portable, version-controlled CMMN XML that the firm owns.

Crunchafi does not need to be an audit methodology expert to build this. The platform is methodology-agnostic by design. The firm brings the knowledge; Case-Flow provides the engine and the tools to encode it.

Every firm sits somewhere on a spectrum, and Case-Flow serves all of them:

- **Use a standard template** — firms that want a proven starting point select a pre-built engagement template from the marketplace, aligned with ISA/GAAS or contributed by a design partner, and run it as-is
- **Customise an existing template** — firms that mostly follow a standard methodology but have specific adaptations load a template into the modeller and adjust it: adding required procedures, changing sign-off rules, encoding their specific risk thresholds
- **Build from scratch** — firms with deeply proprietary methodologies developed over decades model their process entirely in the CMMN modeller; the platform imposes no structure, only the standard they choose to follow

Critically, **firms switching from a proprietary platform do not have to abandon their existing methodology.** A firm currently running engagements in Thomson Reuters' Guided Assurance using PPC can encode that same workflow in Case-Flow and migrate without starting over. Their process is already defined — Case-Flow gives them a place to own it rather than rent it. Over time they can adapt and extend it. The methodology travels with the firm, not with the vendor.

For firms that *do* want a starting point, Crunchafi can offer standard templates — either built with design partners or aligned with published audit standards. But the key word is *starting point*. Firms customise, extend, and own the result. That is the opposite of being locked into PPC.

**This also substantially reduces Crunchafi's dependency on audit domain expertise at launch.** Rather than Crunchafi having to define what a financial audit looks like, early design partner firms do that work in collaboration. Their methodology becomes the first template. The platform ships with real, validated content without Crunchafi having to build deep audit knowledge internally.

---

## Why This Is a Platform, Not Just an Audit Tool

Audit is the beachhead — it is where Crunchafi's products and customer relationships already are. But the engine is domain-agnostic. The same platform that orchestrates a financial audit engagement orchestrates any knowledge-intensive, human-driven process where the sequence of work cannot be fully predetermined.

| Vertical | Case = | Why it fits |
|---|---|---|
| **Financial audit** | Audit engagement | Variable procedures, findings-driven, multi-party sign-off |
| **Legal / matter management** | Legal matter | Unpredictable, knowledge-driven, evidence-intensive |
| **HR case management** | Employee relations case | Disciplinary, grievance, accommodation — variable paths |
| **Insurance claims** | Claim | Evidence gathering, adjuster decisions, parallel workstreams |
| **Financial services compliance** | AML / fraud investigation | Required + discretionary procedures, escalation paths |
| **Healthcare** | Patient care plan | Clinical decisions, variable care pathways |

This broadens the TAM considerably and also reframes the domain knowledge concern: Crunchafi does not need to be an expert in any of these verticals. The platform provides the engine and the modeller. Domain experts — whether CPA firms, law firms, or HR teams — encode their own process. Crunchafi's role is to provide the infrastructure they build on.

Audit is where we go to market first. Platform extensibility is how we grow.

---

## Why CMMN Is the Right Foundation

CMMN (Case Management Model and Notation) is an OMG open standard specifically designed for knowledge-intensive, adaptive work — exactly what a financial audit is. Unlike rigid process flows (BPMN), CMMN lets the auditor decide what to do next based on what they find.

| CMMN Concept | Audit Equivalent |
|---|---|
| Case | Audit engagement |
| Stage | Planning, Fieldwork, Reporting, Completion |
| Required task | Mandatory ISA/GAAS procedures |
| Discretionary task | Extended procedures triggered by a finding |
| Entry criterion | "Start fieldwork once planning is signed off" |
| Milestone | "Risk assessment complete", "Partner sign-off received" |
| Repetition rule | Re-test a control after failure |
| Manual activation | Partner must explicitly approve before proceeding |

Building on an open standard means:
- Firms can export their audit programs as CMMN XML — they are never locked in
- The modeller is open-source (`cmmn-js`, maintained by Camunda)
- The standard is auditable by regulators and quality reviewers

---

## Unique Technical Advantages

### Immutable Audit Trail by Architecture

Every state transition in a case is captured as an immutable, sequenced, timestamped event via Orleans event sourcing. This is not a log written as an afterthought — it is the source of truth from which all state is derived. Events cannot be modified or deleted.

This means Crunchafi can offer something no competitor currently does: **a cryptographically sound, append-only record of every action taken in every engagement, stored permanently.**

### Point-in-Time Reconstruction

Because the full event history is retained, the system can reconstruct the exact state of any engagement at any point in time.

*"What was complete at the moment the partner signed the opinion?"*
*"What procedures had been reviewed when the manager flagged the revenue risk?"*

This directly addresses ISA 230 (Audit Documentation) and PCAOB AS 1215 requirements that firms demonstrate their documentation supported conclusions reached **at the time** — not as reconstructed later. No competing platform offers this based on a standards-compliant event-sourced architecture.

### Firms Encode Their Own Methodology

Thomson Reuters embeds PPC methodology — firms use TR's audit programs, TR's structure, TR's sequence. Large regionals, specialist firms, and international practices have their own proprietary methodologies developed over decades. They do not use PPC and do not want to.

Case-Flow lets a firm build their engagement workflow once in the CMMN modeller and run it consistently across every engagement. The firm owns the methodology. It is stored as portable CMMN XML, not locked in a vendor's proprietary format.

### Scales to Thousands of Concurrent Engagements

The Orleans virtual actor model means each case instance, each plan item, and each sentry is an independent in-memory actor. There is no shared database row locking, no polling table, no scheduler scanning for work. Thousands of concurrent engagements run independently with no interference. Horizontal scale is add-a-container.

### Acquisition Integration Is Structural, Not Bespoke

When Crunchafi acquires a new product, integrating it into Case-Flow is a matter of implementing a `ProcessTask` — a defined interface that invokes an external API, waits for a result, and feeds it back into the case. This is not a custom integration project for each acquisition. It is the same pattern every time.

### Product-Agnostic Adapter Architecture

The ProcessTask adapter interface is not Crunchafi-specific. Any product — whether Crunchafi-owned or third-party — can implement it. Crunchafi ships first-party adapters for Lease Accounting and Data Extraction on day one. Third-party vendors who want to be part of a firm's workflow implement the same interface and list on the marketplace.

This positions Crunchafi as a **platform operator**, not a walled garden. Firms are not restricted to Crunchafi tools within their workflows. That openness is a selling point — it removes adoption friction — while Crunchafi's bundled products retain the preferred position through pricing and deep integration. The architecture enables an ecosystem. Crunchafi decides how open or closed to make the marketplace, and can adjust that policy as the platform matures.

---

## The Market Position

### Who Are We Selling To?

**Near-term target:** Mid-market CPA firms with their own audit methodology who are underserved by TR's PPC-centric approach. Firms with 20–200 professionals who want consistency and auditability across engagements without an enterprise procurement process.

**Medium-term target:** Large regional and specialist firms who have proprietary methodologies and resent being forced into PPC. These firms have the budget and the pain.

**Long-term:** Any firm using Crunchafi products today is a warm lead — they are already in the ecosystem, already using Lease Accounting or Data Extraction, and Case-Flow adds value on top of what they already have.

### Competitive Landscape

| Competitor | Approach | Our Advantage |
|---|---|---|
| Thomson Reuters Guided Assurance | Proprietary, PPC methodology, curated partners | Open standard, firm's own methodology, open ecosystem |
| Wolters Kluwer TeamMate+ | Strong internal audit, less CPA firm focus | Built specifically for external audit engagements |
| AuditBoard | GRC / SOX focus | Designed for financial statement audit |
| CaseWare | Legacy architecture, file-based | Cloud-native, event-sourced, real-time collaboration |
| Spreadsheets / email | The actual status quo for many firms | Anything is better; easy displacement |

Every competitor on this list has built a workflow platform. The question is not whether audit workflow software exists — it does. The question is who owns the workflow.

Thomson Reuters owns it. CaseWare owns it. TeamMate+ owns it. Every incumbent made the same architectural decision: embed a proprietary workflow that firms adopt rather than define. That decision creates stickiness for the vendor. It also means every firm on those platforms is running someone else's audit program.

CMMN changes the ownership model. It is an open standard — not Crunchafi's standard, not a proprietary format, but a published OMG specification that any tool can read. A firm's audit methodology, encoded in CMMN, is portable XML they own and can version-control, export, inspect, and defend to a regulator. Crunchafi can offer standard templates as a starting point — aligned with ISA/GAAS or contributed by design partners — while still allowing any firm to extend, customise, and fully own the result.

No incumbent can offer this without rebuilding on open standards and giving up the lock-in that their business model depends on. That is the gap — not that workflow software doesn't exist, but that workflow software that gives firms genuine ownership does not.

---

## Revenue Model

### Pricing Unit: Per Engagement

The natural pricing unit is the **audit engagement** — consistent with Lease Accounting's per-lease model and aligned with how CPA firms think about their work. The cost scales with the firm's revenue, is directly billable to each client engagement, and is easy to budget and justify.

CPA firms price their audit engagements at $10,000–$500,000+ depending on entity size. Software that enables and documents that work should cost 1–3% of the value it enables. The pricing below is conservative by that measure.

### Tier Structure

**Standard — $300–400/engagement/year**
- Core engagement workflow
- Lease Accounting + Data Extraction ProcessTasks included
- Standard audit trail and reporting
- Shared infrastructure
- Email support

**Professional — $600–800/engagement/year**
- Everything in Standard
- Point-in-time audit trail reconstruction
- Exportable immutable audit evidence (regulatory-grade PDF/Excel)
- Dedicated infrastructure (isolated stream topics per tenant)
- API access for custom integrations
- Priority support

**Enterprise — custom**
- Volume discounts above 200 engagements/year
- Custom ProcessTask integrations for third-party products
- Custom SLA and dedicated customer success manager
- On-premise data residency option for regulated markets

### Packaging Alternative: Annual Subscription Buckets

For firms that prefer predictable monthly spend over per-engagement billing:

| Plan | Price | Engagements Included |
|---|---|---|
| Starter | $500/month | Up to 15 |
| Growth | $1,500/month | Up to 50 |
| Scale | $3,500/month | Up to 150 |
| Enterprise | Custom | Unlimited |

Overage at $35–50 per engagement above the bucket limit.

### Bundle Strategy

Firms using Lease Accounting + Data Extraction + Case-Flow receive a 20% discount across all three products. This creates a meaningful retention mechanism — switching workflow platforms becomes the switching cost for all Crunchafi products simultaneously. It also gives the sales team a reason to lead with the platform story rather than selling products individually.

### Revenue Projections

Crunchafi has approximately 750 firms in its existing customer base. These are warm leads — they already trust Crunchafi and use at least one product.

| Conversion rate | Avg plan | ARR |
|---|---|---|
| 5% (37 firms) | Growth ($18k/year) | **$670k** |
| 10% (75 firms) | Growth ($18k/year) | **$1.35M** |
| 20% (150 firms) | Growth ($18k/year) | **$2.7M** |
| 10% (75 firms) | Scale ($42k/year) | **$3.15M** |

A 10% conversion of the existing customer base at the Growth plan tier = **$1.35M ARR from relationships Crunchafi already has**, before acquiring a single new customer.

These projections assume no enterprise deals, no new customer acquisition, and no upsell from Standard to Professional. Each of those is upside.

### Template Marketplace (Future)

Firms and methodology providers publish CMMN audit program templates. Crunchafi takes a 20–30% revenue share. This creates a compounding network effect: better templates attract more firms; more firms create demand for more templates. The platform becomes more valuable to every participant as it grows — a dynamic that no individual product achieves alone.

### Design Partner Approach

The first 3–5 firms receive 60–70% off in exchange for:
- Co-developing the first audit engagement template
- Named case study and reference call rights
- A 2–3 year commitment

The template they help build becomes the primary sales asset for the next 50 customers. Their investment in the methodology pays dividends far beyond their own firm.

For design partners, Crunchafi provides white-glove onboarding: the firm's audit methodology is modelled collaboratively by the Crunchafi team using standard CMMN tooling, and the resulting workflow is loaded directly into the platform. Design partners do not need to learn a modelling tool — they describe their process, Crunchafi encodes it. This concierge approach delivers a running engagement workflow faster, generates validated templates that become the marketplace's opening inventory, and ensures the self-service modeller — when built — reflects how real firms actually work rather than how a product team imagined they would.

---

## Build vs. Buy

Crunchafi's stated growth strategy is acquisition. The company has spent the better part of two years evaluating products that could expand its position in the financial audit process. That is the right instinct. The question is whether the right acquisition has been on the market.

External acquisitions carry well-understood risks:

- **Valuation premium** — acquirers pay for revenue, not for the underlying technical work
- **Technical debt unknown** — due diligence reduces but never eliminates architectural surprises
- **Integration cost** — every external acquisition requires custom work to connect it to Crunchafi's stack
- **Cultural and operational fit** — a new team, a new codebase, a new process

There is a third option that avoids all of these.

**The CMMN 1.1 engine described in this document already exists.** It was built by someone Crunchafi has known and worked with for nearly a decade. The technical quality is not an unknown — the codebase has been evaluated in full, the architecture is understood, and the gaps are documented. The work required to reach production is well-scoped: 2–3 months with a small team.

Acquiring this asset internally is not comparable to building from scratch. It is also not comparable to a typical external acquisition. It is acquiring a working, spec-compliant CMMN runtime from a trusted source — at a fraction of what a comparable capability would cost externally — with no integration unknowns, no cultural friction, and a builder who understands Crunchafi's stack and strategic direction.

**IP provenance is clean.** The engine was developed independently, prior to and outside the scope of any work performed for Crunchafi. It was not built on Crunchafi time, under Crunchafi direction, or within the scope of any contracted duties. Ownership is unambiguous.

The alternative — continuing to shop externally for workflow orchestration capabilities — is likely to cost more, take longer, and deliver something that fits less well.

---

## How Firms Get Started

Getting firms productive quickly is a product design problem as much as a technical one. The solution is a combination of Crunchafi's existing platform infrastructure, a curated template library, and a modeller that lowers the barrier to workflow definition.

**Phase one: concierge onboarding for design partners.** The self-service modeller is a v2 feature, deliberately. For the first cohort of firms, Crunchafi models the workflow collaboratively — the firm describes their audit methodology, Crunchafi encodes it in CMMN using standard open source tooling, and the resulting XML is loaded directly into the platform via API. The firm never touches a modelling tool; they go straight to running real engagements through the dashboard. This approach generates validated templates from actual firm methodologies before the self-service layer is built, and ensures the modeller is designed around how firms actually work.

**Phase two: self-service via Carina.** Carina is already Crunchafi's identity and access spine and the logical surface for platform-level features that span products. The workflow modeller and template marketplace are natural extensions of what Carina already does — platform tools that CPA firms already access through Carina. Hosting Case-Flow's user-facing layer within Carina minimises onboarding cost for existing customers: they are already authenticated, already credentialed, already oriented to the platform. When a firm is ready to customise beyond a template, they open the modeller in Carina — the same place they manage everything else.

**The template library is the on-ramp for self-service.** A firm does not need to build a workflow from scratch. They browse the marketplace, select a template built by a design partner or aligned with published ISA/GAAS standards, and load it into the modeller. From there they customise: add their firm's specific procedures, adjust required vs. discretionary designations, encode their sign-off rules. The result is their methodology, built in hours, not a project.

**Products as first-class adapters.** Every product that participates in a Case-Flow engagement implements a ProcessTask adapter — a defined interface that accepts inputs, invokes the product's API, and returns results to the case. Crunchafi ships first-party adapters for Lease Accounting and Data Extraction on day one. Third-party products implement the same interface. A firm's workflow is not limited to Crunchafi products — it can invoke any tool that exposes the adapter interface.

This means the platform is **product-agnostic by design**. Crunchafi's own products get preferential placement (bundled, pre-configured, deeply integrated), but the architecture does not require it. A firm using a different data extraction tool can still use Case-Flow. That removes the "we only work with Crunchafi products" objection and broadens the addressable market substantially.

---

## What We Are Asking For

This is a decision about whether Crunchafi acquires the orchestration layer that turns its products into a platform — or continues to source that function from Thomson Reuters.

The asset exists. The question is whether Crunchafi moves first.

**Option A — Acquire the engine and build the product**

1. **Formalise the acquisition** — define terms for acquiring the Case-Flow CMMN engine as a Crunchafi IP asset; assign it a product name and executive sponsorship
2. **Allocate a small team for 3 months** — 2–3 developers led by the original author as architect and technical lead, leveraging existing Crunchafi infrastructure (Azure, Auth0, Bicep, ScrumBot)
3. **Define the first design partner** — sales and client success identify one firm from the existing early adopter base willing to co-build the first audit engagement template; launch with real, validated content

**v1 delivers a running audit engagement platform:**
- Engine modernised to .NET 8 / Orleans 8 on Azure Container Apps
- REST API with CMMN XML ingestion — workflows loaded directly via API
- Multi-tenant authentication via Carina
- Engagement dashboard — active cases, workflow progress, stage completion
- Task inbox — assigned tasks, blocking items, what needs action today
- Document attachment via Azure Blob Storage
- Webhook notifications for task assignment and completion
- First audit engagement template, built with the design partner

The self-service CMMN modeller and template marketplace ship in v2, informed by what design partners actually build. This is the deliberate sequencing — ship the engine and the daily-use surface first; build the self-service authoring tools once the first templates exist.

**Option B — Continue deepening the TR partnership**

Remain a component vendor in Thomson Reuters' Guided Assurance. Accept the ceiling on revenue, strategic positioning, and platform ownership that comes with it.

The engine exists. The infrastructure patterns exist. The customer relationships exist. The market gap is real. The ask is to connect them.

---

## Risk Assessment

| Risk | Mitigation |
|---|---|
| TR partnership tension | Lease Accounting continues as a TR partner regardless. Case-Flow serves firms who want to own their workflow — a different buyer motion than TR's PPC-embedded approach. Near-term complementary; strategic hedge long-term. |
| Over-reliance on TR if we do nothing | This is the risk of *not* building Case-Flow. TR already chose Validis over Data Extraction. Remaining a component vendor without platform ownership compounds this exposure. |
| Domain knowledge — we are not audit experts | By design. The platform is methodology-agnostic. Design partner firms encode their own methodology. Crunchafi ships the engine and the tools; firms bring the knowledge. |
| Engine modernisation is harder than expected | Orleans 3→8 serialisation migration is the main technical risk; isolated, testable, does not block API or UI work in parallel. |
| Adoption is slow without content | Design partner approach — first template built *with* a firm, not *for* them. Their investment in the methodology sells it to the next 50 customers. |
| Competing priorities | 2–3 months is a contained investment; ScrumBot can absorb mechanical work and accelerate delivery. |
| Security and compliance — CPA firms require it | **SOC 2 Type II compliance is required and achievable.** The event-sourced architecture produces a naturally append-only, tamper-evident audit trail that satisfies processing integrity and availability criteria by design — not as a bolt-on. A SOC 2 engagement should be scoped into the roadmap alongside the first production launch. |

---

## Summary

Crunchafi has a working CMMN engine, an established Azure infrastructure platform, deep CPA firm relationships, and a strategic direction toward audit process ownership. Case-Flow is the connective layer that turns those assets into a platform.

The technical foundation is built. The market gap is real. The investment is contained.

**The ask is 2–3 developers for 3 months.** The realistic return is $1.35M ARR from existing customers at 10% conversion — before a single new customer is acquired, before enterprise deals, before the template marketplace, and before the compounding value of every future acquisition plugging into the platform on day one.

The alternative is to remain a component vendor in someone else's platform indefinitely, with a ceiling on both revenue and strategic positioning that compounds over time.

This is a contained bet with asymmetric upside. The engine exists. The infrastructure exists. The customers exist. The gap in the market exists. The question is whether Crunchafi moves first.
