# Wayfinder — Azure DevOps → GitHub migration

The project moved from Azure DevOps (`brute-force/Case/Case.Flow`) to GitHub
(`en-gen/wayfinder`, private) and was renamed **Case.Flow → Wayfinder**.

Azure DevOps is now the **legacy/reference** system. GitHub is authoritative.

---

## ⚠️ Actions that need you

Two things cannot be automated (interactive auth / paid plan). Everything blocked on
them is already written and committed — each is a single step.

### 1. Grant the `workflow` OAuth scope — unblocks CI/CD

```bash
gh auth refresh -h github.com -s workflow
```

The local `gh` token has `gist, read:org, repo`. GitHub **rejects any push touching
`.github/workflows/*`** without the `workflow` scope, so the CI workflow cannot be
pushed. It is fully written and reviewed — after this command it is one push away.

### 2. Branch protection — decide

Protected branches / required status checks are **not available on private repos on the
GitHub Free plan** (`403: Upgrade to GitHub Pro or make this repository public`).

Note this is a **regression from Azure DevOps**, whose free tier *did* provide branch
policies on private repos.

Options:

- **Upgrade `en-gen` to GitHub Team** (~$4/active user/month) — enables branch protection,
  required status checks, and enforced GitFlow. Cheapest path to parity.
- **Convention-only** — no enforced gate. CI still runs on every PR; it just cannot be
  *required* before merge. The existing workflow (agent pushes a branch → PR → human
  merges) already covers most of this by discipline.
- **Go public** — protection is free on public repos, but that is downstream of the
  licensing/open-source decision, which is not settled.

### 3. Optional — Codecov token

The repo is private, so tokenless Codecov upload will not work. Coverage upload is wired
with `token: ${{ secrets.CODECOV_TOKEN }}` and is non-blocking (`fail_ci_if_error: false`),
so CI passes without it — but coverage will not land until the secret is added.

---

## Parity: what moved

| Azure DevOps | GitHub counterpart | Status |
|---|---|---|
| Git repo + full history | `en-gen/wayfinder` | ✅ Done (all branches, 64 commits) |
| Default branch / release branch | `develop` default, `main` releases (GitFlow) | ✅ Done |
| Repo settings | Issues on, wiki/projects off, delete-branch-on-merge, squash + merge | ✅ Done |
| Build pipeline (`devops/build/case.flow.ci.yml`) | `.github/workflows/ci.yml` | ⏸ Written + reviewed — **blocked on `workflow` scope** |
| CD pipeline (`Case.Flow.CD` — never authored in ADO) | `.github/workflows/cd.yml` | ⏳ To draft (same scope blocker) |
| Branch policies (build validation on develop/main) | Branch protection / rulesets | ⛔ **Blocked — needs paid plan** (see above) |
| Work items (138, full hierarchy) | Issues + Milestones + sub-issues + labels | ✅ **Done** — 130 issues (80 open / 50 closed), 8 milestones, 18 labels, 5 sub-issue links, 60 comments |
| Project wiki | `docs/` in-repo + GitHub Issues | ✅ Docs already in-repo; wiki disabled |
| README / issue + PR templates / CODEOWNERS / Dependabot | `.github/*`, `README.md` | ⏳ In review (`chore/repo-scaffolding`) |
| NuGet Audit gate (moderate+ advisories fail the build) | Unchanged — `Directory.Build.props` | ✅ Moved with the code |
| Format gate (Husky pre-commit + CI check) | Branch `chore/husky-format-hooks` (was ADO PR 54) | ⏳ Re-open as a GitHub PR |
| GitVersion semantic versioning | Unchanged — `GitVersion.yml` | ✅ Moved with the code |

### Work-item mapping

- **ADO Epic** → GitHub **Milestone**
- **Feature / Task / Bug / Issue** → GitHub **Issue**, assigned to its ancestor epic's milestone
- **Parent/child hierarchy** → GitHub **sub-issues**
- **Type, priority (P1/P2/P3), tags** → **labels**
- **State**: New/Active/Resolved → open (Resolved labelled); Closed → closed; Removed → closed + `removed`
- Original ADO id and links are preserved in each issue body for traceability.

**Verified after the run** (checked against GitHub directly, not the migration script's own summary):
130 issues — 80 open / 50 closed; 121 carry a milestone and the other 9 are exactly the legacy
block (ADO #1–8, #71), which correctly have none. Issue bodies are **byte-verbatim** ADO
descriptions converted to Markdown — an audit found 60 of 130 were not byte-exact on the first
pass (HTML-entity artifacts plus some paraphrase) and all were re-fetched from ADO and replaced.
Epic-level comments, which have nowhere to live once epics become milestones, were folded into
the milestone descriptions — including M1's "REOPENING" note, so the record of *why* that
milestone is open survives the move.

---

## Follow-ups (tracked, not blockers)

- **Code rename** — `Flow.*` → `Wayfinder.*`, `src/CaseFlow.sln` → `src/Wayfinder.sln`,
  GitVersion `tag-prefix: Case.Flow-` → `Wayfinder-`. The repo and docs already say
  Wayfinder; the namespaces do not yet. Tracked as its own PR.
- **Re-open the format-gate PR** (Husky) on GitHub once CI exists, so it validates here.
- **Milestones are semver** — `0.1.0` (engine complete) … `1.0.0` (GA). See the roadmap.

---

## Notes

- Nothing was deleted from Azure DevOps. It remains intact as the legacy record.
- The `origin` remote still points at Azure DevOps; `github` points at `en-gen/wayfinder`.
  Re-point or remove `origin` once you are confident in the move.
