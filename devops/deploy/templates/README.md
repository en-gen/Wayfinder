# devops/deploy

Deploy pipelines live here, separate from build by convention — build and deploy
are always distinct pipeline definitions.

- `case.flow.cd.yml` (planned, roadmap M2 #49/#34): downloads build artifacts,
  deploys infrastructure, promotes container images. Triggered by Case.Flow.CI
  completion, never by branch push.
- `templates/`: reusable step templates shared by deploy pipelines.
