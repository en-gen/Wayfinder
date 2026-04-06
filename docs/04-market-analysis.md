# Market Analysis

*Assessed April 2026.*

---

## What Is the Market?

CMMN-based products address **knowledge-intensive work management** — situations where humans make decisions about what to do next based on what they discover, rather than following a predefined process flow. The target buyers are organizations that manage complex, variable work involving multiple people, documents, decisions, and compliance requirements.

---

## Existing CMMN Implementations

### Products with Genuine CMMN Support

| Product | Vendor | Notes |
|---|---|---|
| Trisotech Digital Enterprise Suite | Trisotech | Strongest OMG standards compliance; BPMN+CMMN+DMN unified |
| IBM Business Automation Workflow | IBM | Enterprise-grade; part of Cloud Pak for Business Automation |
| Flowable | Open source | Most active open-source CMMN runtime; weak SaaS/cloud-native story |
| OpenText MBPM | OpenText | Acquired Cordys which had strong CMMN support |

### Partial/Deprecated CMMN

| Product | Notes |
|---|---|
| Camunda 7 | Had a CMMN engine — **removed in Camunda 8**. Camunda 7 reaches end-of-life 2025, leaving an orphan community. |
| Activiti / Alfresco | Fork lineage of Camunda; some CMMN support |

### Market Signal: Camunda Dropped CMMN

When the dominant open-source BPM vendor removes CMMN support due to low adoption, it tells you the addressable market for *generic* CMMN is small. However, it also creates a migration opportunity — Camunda 7 customers need somewhere to go, and Flowable is the main open-source option without a strong cloud-native SaaS offering.

---

## The Honest Challenge

**CMMN adoption has been limited** relative to BPMN. Reasons:
- Most business buyers don't know what CMMN is; they buy "case management"
- Enterprise incumbents (IBM, Pega, ServiceNow) have deep relationships and proprietary implementations
- The OMG spec is complex and tooling never reached BPMN-level maturity

**The winning strategy is not "a CMMN platform."** It is a vertical-specific product that uses CMMN as an architectural foundation while presenting as a domain solution to buyers. Standards compliance becomes a trust and portability argument, not the product pitch.

---

## Target Verticals

| Vertical | CMMN Fit | Competition | Notes |
|---|---|---|---|
| **Financial Audit** | Excellent | Moderate | TeamMate, AuditBoard, Workiva; no standards-based option |
| **Legal / Matter Management** | Excellent | Moderate | Underserved at mid-market |
| **HR Case Management** | Good | Low | Disciplinary, grievance, accommodation cases; genuinely underserved |
| **Healthcare** | Excellent | High | Strong fit but HIPAA adds cost before first sale |
| **Insurance Claims** | Excellent | High | Guidewire, Duck Creek dominate; hard to break in |
| **Government / Public Sector** | Good | Low–moderate | Long sales cycles, procurement complexity |

---

## Financial Audit — Deep Dive

### Why Audit Is a Strong Fit

A financial audit engagement is textbook CMMN territory:
- The auditor decides what to examine based on what they find
- Some procedures are always required (ISA/GAAS mandatory); others are discretionary based on risk
- Work items can be revisited, escalated, or deferred
- Completion depends on evidence gathered, not a fixed sequence

### CMMN Concept → Audit Mapping

| CMMN Concept | Audit Equivalent |
|---|---|
| Case | Audit engagement |
| Stage | Audit phases (Planning, Fieldwork, Reporting, Completion) |
| HumanTask | Control test, substantive procedure, client interview |
| ProcessTask | Crunchafi Lease Accounting analysis, Strongbox ERP extraction |
| Milestone | "Risk Assessment Complete", "Draft Report Approved" |
| Entry criterion | "Start fieldwork once planning is signed off" |
| Exit criterion | "Close fieldwork stage once all required procedures are complete" |
| Discretionary task | Extended procedures triggered by a finding |
| RequiredRule | Mandatory ISA/GAAS procedures |
| RepetitionRule | Re-test a control after failure |
| ManualActivationRule | Partner must explicitly sign off before proceeding |
| Case file | The audit engagement file — trial balance, workpapers, client docs |

### Example Case Model Structure

```
[Case: FY2025 Audit — Acme Corp]
  │
  ├─ [Stage: Planning]
  │    ├─ [Task: Assess materiality]              (required)
  │    ├─ [Task: Identify risk areas]             (required)
  │    ├─ [ProcessTask: Strongbox — ERP extract]  (required)
  │    ├─ [Task: Prepare audit plan]              (required)
  │    └─ [Milestone: Planning Complete]
  │
  ├─ [Stage: Fieldwork]  ← entry: Planning Complete
  │    ├─ [Stage: Revenue Testing]
  │    │    ├─ [Task: Test revenue controls]      (required)
  │    │    ├─ [Task: Substantive testing]        (required)
  │    │    └─ [Task: Extended sampling]          (discretionary — if control failure)
  │    ├─ [Stage: Lease Review]
  │    │    └─ [ProcessTask: Crunchafi Lease Accounting]         (required if leases exist)
  │    ├─ [Stage: Cash Flow Review]
  │    └─ [Stage: IT Controls]                   (discretionary — if significant IT risk)
  │
  ├─ [Stage: Reporting]  ← entry: Fieldwork Complete
  │    ├─ [Task: Draft management letter]
  │    ├─ [Task: Internal review]
  │    └─ [Task: Client response review]
  │
  └─ [Stage: Completion]
       ├─ [Task: Final sign-off]                  (ManualActivationRule — partner only)
       └─ [Milestone: Audit Complete]
```

### Audit Software Competitive Landscape

| Product | Vendor | Type | Notes |
|---|---|---|---|
| Guided Assurance (Cloud Audit Suite) | Thomson Reuters | Audit workflow platform | PPC methodology + Fieldguide; AI-powered |
| AdvanceFlow | Thomson Reuters | Audit workflow | Older product; being superseded by Guided Assurance |
| TeamMate+ | Wolters Kluwer | Audit management | Strong internal audit; less CPA firm focus |
| AuditBoard | AuditBoard | GRC + audit | Strong for internal audit / SOX |
| Workiva | Workiva | Financial reporting + audit | Compliance documentation focus |
| CaseWare | CaseWare | Audit + accounting | Mid-market CPA firms |

**Key gap:** None of these are standards-based. A CMMN-native audit engagement platform has a genuine differentiation story around portability and customizability.

---

## Crunchafi-Specific Strategic Context

### Current Position

Crunchafi operates three products targeting the same buyer (CPA firms and lenders):
- **Crunchafi Lease Accounting** — lease accounting compliance (ASC 842, IFRS 16, GASB)
- **Crunchafi Data Extraction** — financial data extraction from ERPs
- **Cash Flow** — cash flow forecasting and analysis

These products are currently **silos** — a firm uses each independently, with no thread connecting them into a coherent engagement workflow.

### The Thomson Reuters Partnership

In December 2025, Crunchafi integrated Crunchafi Lease Accounting into Thomson Reuters' **Guided Assurance** (Cloud Audit Suite) as a ProcessTask within their PPC-based audit workflow. The partnership deepened in February 2026 with joint education initiatives.

**Key observation:** Thomson Reuters chose **Validis** (not Crunchafi Data Extraction) as their data extraction partner for Audit Intelligence. The two are direct competitors in the ERP-to-audit-data pipeline.

### The CMMN Opportunity Within Crunchafi

A CMMN engine could serve as the **orchestration layer** that stitches Crunchafi's products together into a unified audit engagement platform:

```
[Audit Engagement Case — powered by Case-Flow]
        │
        ├─ ProcessTask: Strongbox      → client ERP extraction
        ├─ ProcessTask: Crunchafi Lease Accounting    → lease accounting analysis
        ├─ ProcessTask: Cash Flow      → cash flow review
        └─ ProcessTask: [Future]       → next acquisition plugs in here
```

**Strategic value:**
1. **Acquisition glue** — every product Crunchafi acquires becomes a ProcessTask
2. **Stickiness** — once a firm's audit workflow lives in CMMN, switching individual products becomes very costly
3. **Competitive pressure on Validis** — an integrated Crunchafi Data Extraction ProcessTask within the engagement platform competes more effectively with Validis than the product does standalone
4. **Platform positioning** — shifts Crunchafi from component vendor to platform owner

### Two Strategic Paths

**Path A — Deepen the TR Partnership**
Stay a component vendor within Thomson Reuters' Guided Assurance. Get Crunchafi Data Extraction into the TR ecosystem alongside Validis. Let TR own the orchestration layer.
- Lower sales friction (TR's 750+ firm relationships)
- Permanently capped upside — always a line item, never the platform
- Vulnerable to TR acquiring a competitor or deprioritizing the partnership

**Path B — Build the Orchestration Layer**
Use Case-Flow to build the audit engagement platform that sits above TR's tools. CPA firms run their engagements in Crunchafi; TR products, Strongbox, Crunchafi Lease Accounting, and future acquisitions are all ProcessTasks.
- Platform stickiness and upsell surface
- Directly challenges Thomson Reuters' Guided Assurance
- Risks the existing partnership
- Requires significant investment in the engagement workflow product

**Practical first step:** Neither path requires an immediate binary choice. Building the CMMN layer internally — connecting Crunchafi's own products — creates platform value without directly competing with TR. That foundation keeps both options open.

---

## Positioning Recommendation

Do not market this as "a CMMN platform." Market it as:

> **"The audit engagement platform for CPA firms — built on open standards so you're never locked in."**

CMMN compliance is a trust argument (auditability, portability, no proprietary lock-in), not the product pitch. The product pitch is: one place to run an audit engagement, with all your tools connected, and your methodology encoded so every engagement is consistent.

The template library (pre-built audit engagement models) is the wedge — it signals domain expertise and dramatically lowers time-to-value for early customers.
