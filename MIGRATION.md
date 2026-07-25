# Wayfinder — Azure DevOps → GitHub migration

The project moved from Azure DevOps (`brute-force/Case/Case.Flow`) to GitHub
(`en-gen/Wayfinder`) and was renamed **Case.Flow → Wayfinder**. The repository is
**public**, licensed under the [Business Source License 1.1](LICENSE) — source-available,
not free for commercial use.

Azure DevOps is now the **legacy/reference** system. GitHub is authoritative.

The migration is complete. This document is kept because issue bodies still carry their
original ADO identifiers, and this is the map for anyone chasing one of those references.

---

## ⚠️ The one action that still needs a human

### Grant the `workflow` OAuth scope — unblocks CI/CD

```bash
gh auth refresh -h github.com -s workflow
```

The local `gh` token has `gist, read:org, repo`. GitHub **rejects any push touching
`.github/workflows/*`** without the `workflow` scope. Four commits are written, reviewed,
and waiting on the `ci/github-actions` branch: CI (with a `dotnet format` gate), the
path-filtered triggers, CD to ghcr.io, plus CodeQL and the issue-closing workflow. After
this command they are one push away.

### Resolved

- **Branch protection** — done. `develop` and `main` are covered by the
  `protect-develop-and-main` ruleset (no deletion, no force-push, PR required). This was
  blocked while the repo was private on the Free plan; going public made it free.
- **Codecov token** — no longer needed. Tokenless upload works on public repositories.

---

## Parity: what moved

| Azure DevOps | GitHub counterpart | Status |
|---|---|---|
| Git repo + full history | `en-gen/Wayfinder` (public) | ✅ Done (all branches, 79 commits) |
| Default branch / release branch | `develop` default, `main` releases (GitFlow) | ✅ Done |
| Repo settings | Issues on, wiki/projects off, delete-branch-on-merge, squash + merge | ✅ Done |
| Build pipeline (`devops/build/case.flow.ci.yml`) | `.github/workflows/ci.yml` | ⏸ Written + reviewed — **blocked on `workflow` scope** |
| CD pipeline (`Case.Flow.CD` — never authored in ADO) | `.github/workflows/cd.yml` | ⏸ Written — same scope blocker |
| Branch policies (build validation on develop/main) | Ruleset `protect-develop-and-main` | ✅ Done — free once the repo went public |
| Work items (138, full hierarchy) | Issues + Milestones + sub-issues + labels | ✅ **Done** — 130 issues (80 open / 50 closed), 8 milestones, 18 labels, 5 sub-issue links, 60 comments |
| Project wiki | `docs/` in-repo + GitHub Issues | ✅ Docs already in-repo; wiki disabled |
| README / issue + PR templates / CODEOWNERS / Dependabot | `.github/*`, `README.md` | ✅ Merged (#1) |
| NuGet Audit gate (moderate+ advisories fail the build) | Unchanged — `Directory.Build.props` | ✅ Moved with the code |
| Format gate (Husky pre-commit + CI check) | Husky hook merged (#3); CI check on `ci/github-actions` | ⏸ Hook done, CI half blocked on scope |
| GitVersion semantic versioning | Unchanged — `GitVersion.yml` | ✅ Moved with the code |
| License / governance | `LICENSE` (BUSL-1.1), `SECURITY.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `CHANGELOG.md` | ✅ Merged (#144) — no ADO counterpart |
| Security scanning | CodeQL, secret scanning + push protection, Dependabot alerts | ✅ Enabled (CodeQL workflow pending scope) |

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

- **Re-open the format-gate PR** (Husky) on GitHub once CI exists, so it validates here.
- **Milestones are semver** — `0.1.0` (engine complete) … `1.0.0` (GA). See the roadmap.

---

## Notes

- Nothing was deleted from Azure DevOps. It remains intact as the legacy record. Note that
  the ADO copy predates the history rewrite described below, so it still contains the
  removed planning documents. It is private, but it is not a scrubbed copy.
- The `origin` remote still points at Azure DevOps; `github` points at `en-gen/Wayfinder`.
  Re-point or remove `origin` once you are confident in the move.
