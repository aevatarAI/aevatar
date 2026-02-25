# CI Scripts Map

This directory keeps CI gate scripts and smoke tests.

## Quality Guards

- `tools/ci/coverage_quality_guard.sh`: coverage collection and threshold gate.
- `tools/ci/architecture_guards.sh`: architecture/static guards (includes projection route mapping guard).
- `tools/ci/test_stability_guards.sh`: polling/unstable test pattern guard.
- `tools/ci/solution_split_guards.sh`: split build guard.
- `tools/ci/solution_split_test_guards.sh`: split test guard.
- `tools/ci/projection_route_mapping_guard.sh`: projection reducer routing static guard.
- `tools/ci/restore_and_build.sh`: shared restore/build entry used by CI jobs.
- `tools/ci/event_sourcing_regression.sh`: EventSourcing regression entry (core tests + Orleans/Garnet + architecture guards).

## Integration/Smoke Scripts

- `tools/ci/projection_provider_e2e_smoke.sh`
  - Starts Elasticsearch + Neo4j from `docker-compose.projection-providers.yml`.
  - Waits for readiness, runs `ProjectionProviderE2EIntegrationTests`, and cleans up containers.
- `tools/ci/orleans_garnet_persistence_smoke.sh`: Orleans + Garnet persistence smoke.

## Workflow Mapping

- `.github/workflows/ci.yml`
  - Shared runner preparation is centralized in local action:
    - `.github/actions/prepare-runner/action.yml` (`setup-dotnet` + NuGet cache + optional `ripgrep` install)
  - Job `changes`
    - Uses path filters to detect whether projection-provider or Kafka-runtime integration jobs must run.
  - Job `fast-gates`
    - Runs static architecture and test-stability guards.
  - Job `split-test-guards` (matrix)
    - Runs `dotnet test` for each split solution filter (`foundation/ai/cqrs/workflow/hosting/distributed`).
    - Triggered on `main/dev` pushes, nightly schedule, or manual dispatch.
  - Job `projection-provider-e2e`
    - Runs `tools/ci/projection_provider_e2e_smoke.sh`.
    - Triggered on projection-provider related changes, `main/dev` pushes, or manual dispatch.
  - Job `kafka-transport-integration`
    - Starts Kafka and runs the distributed runtime integration test.
    - Triggered on runtime integration related changes, `main/dev` pushes, or manual dispatch.
  - Job `event-sourcing-regression`
    - Runs `tools/ci/event_sourcing_regression.sh`.
    - Triggered on EventSourcing/runtime related changes, `main/dev` pushes, or manual dispatch.
  - Job `coverage-quality`
    - Runs restore/build + `tools/ci/coverage_quality_guard.sh`.
    - Triggered on `main/dev` pushes, nightly schedule, or manual dispatch.
  - Job `distributed-3node-smoke` -> `tools/ci/distributed_3node_smoke.sh`
