# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `.cmmn` XML import and export, gated by a capability lint that flags unsupported
  constructs at import time rather than failing silently at runtime
- Case file items with full lifecycle, JSON content, and case-file events that drive
  sentries and timer start-triggers
- Deployed multi-silo Orleans clustering — Azure Table cluster membership, durable Azure
  Table reminders, container-aware endpoints, and a three-silo evaluation stack
- Planning tables, discretionary items, and applicability rules
- Business Source License 1.1, security policy, contribution guide, and code of conduct

### Changed

- Project renamed from Case.Flow to Wayfinder. The solution, projects, and namespaces still
  use the `Flow.*` prefix; that rename is tracked separately.
- Project moved from Azure DevOps to GitHub, with work items migrated to Issues and
  Milestones

### Fixed

- Grain reactivation no longer throws "RequestContext TenantId not set" on stream and
  reminder activation
- D10 exit-criterion `OnPart` matching, previously unreachable, now driven by a
  parameterized Exit trigger

### Removed

- Business-planning documents, removed from the working tree and from history

[Unreleased]: https://github.com/en-gen/Wayfinder/commits/develop
