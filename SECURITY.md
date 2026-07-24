# Security Policy

## Supported versions

Wayfinder has not yet cut a 1.0 release. Until it does, **only the tip of `develop`
is supported** — security fixes land there and are picked up by the next release.
Pre-release builds are not intended for production use.

| Version | Supported |
|---|---|
| Tip of `develop` | ✅ |
| Tagged pre-1.0 releases | ❌ (upgrade to the latest) |
| Container images older than the latest tag | ❌ (pull the latest) |

This table will be replaced with a released-version matrix once 1.0 ships.

## Reporting a vulnerability

**Do not open a public GitHub issue for security vulnerabilities.**

Email the maintainer directly at
[2085828+engenb@users.noreply.github.com](mailto:2085828+engenb@users.noreply.github.com)
with the subject line `[Wayfinder] Security vulnerability report`.

Please include:

- A description of the vulnerability and its potential impact
- Steps to reproduce or a minimal proof-of-concept
- The commit hash or container image tag you tested against

## What to expect

- **Acknowledgement** within 5 business days
- **Assessment and severity triage** within 10 business days
- **Resolution or mitigation plan** communicated to you before any public disclosure

We follow a coordinated disclosure model. Please allow reasonable time for a fix to be
prepared and released before disclosing publicly.

## Scope

This policy covers the code in this repository and the container images published from
it. It does not cover vulnerabilities in upstream dependencies (Microsoft Orleans, Jint,
Quartz, and so on) — report those to their respective maintainers.

Two areas are worth calling out because they carry more risk than the rest of the engine:

- **Expression evaluation.** CMMN conditions are evaluated as JavaScript through Jint,
  sandboxed with timeout, statement-count, and memory budgets. A sandbox escape, or any
  input that defeats those budgets, is in scope and should be reported privately.
- **Tenant isolation.** Cases are isolated by Orleans compound grain keys carrying the
  tenant id. Any path that reads or mutates state across a tenant boundary is in scope.

## Responding to a bad release

1. **Deprecate the affected container tag** in the GitHub Packages UI and push a fixed
   image. Published image digests cannot be deleted retroactively in a way that helps
   anyone who already pulled them — assume the bad image stays reachable.
2. **Publish a patch release** immediately with the fix, following the release process in
   [CONTRIBUTING.md](CONTRIBUTING.md).
3. **File a GitHub Security Advisory** if the issue is a security vulnerability: repo
   **Security** tab → **Advisories** → **New draft security advisory**. This can generate a
   CVE and notifies users who have enabled vulnerability alerts.
