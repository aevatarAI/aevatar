# CI Scripts Map

This directory keeps CI gate scripts and smoke tests.

## Quality Guards

- `tools/ci/coverage_quality_guard.sh`: coverage collection and threshold gate (generated files are excluded by default via file filters, e.g. `obj/**`, `Generated/**`, `*.g.cs`).
  - Produces a filtered `Cobertura.xml` under `artifacts/coverage/<timestamp>-ci-gate/report/` after applying assembly/file exclusions for non-core shells/adapters such as `Aevatar.Tools.*`, `Aevatar.Studio.*`, `Aevatar.Authentication.*`, and host app entrypoints.
- `tools/ci/architecture_guards.sh`: architecture/static guards (includes projection route mapping guard, source-regression bans for direct actor `HandleEventAsync` dispatch / raw `SubscribeAsync<EventEnvelope>` outside runtime transport internals, command-observation attach-only lifecycle guard, a focused Web/API forbidden-port guard that blocks loopback URL/defaultPort regressions while avoiding generic numeric timeout/page-size matches, a StreamingProxy deprecation guard that blocks new production consumers, and a workflow actor-query guard requiring `.RequireAuthorization()` or a per-endpoint `security-allowlist` comment on `/api/agents` and `/api/actors/{actorId}*` mappings).
- `tools/ci/agent_profile_governance_guard.sh`: keeps published Agent Profiles Actor-owned, server-sealed and exact-versioned; enforces static route tool sets and rejects legacy file/config authority, process-local registries, query priming, runtime name branches, and per-message policy overrides.
- `tools/ci/audit_trail_guards.sh`: audit/report trail guard; blocks raw payload field names, raw tool args/results write sites, truncation-as-sanitization, and HMAC secret defaults in workflow audit/report paths.
- `tools/ci/catch_exception_observability_guard.sh`: blocks regressions beyond the checked-in baseline for empty broad catches, broad `catch (Exception)` blocks that only log at Debug, and broad return-null fallbacks without visible logging/rethrow/committed failure events.
- `tools/ci/channel_mega_interface_guard.sh`: blocks regressions that merge channel runtime and outbound methods back into one mega interface.
- `tools/ci/frontend_static_boundary_guard.sh`: blocks frontend regressions that call actor-state/replay/projection-refresh endpoints, parse actorId prefixes, or depend on internal EventEnvelope routing fields.
- `tools/ci/fetch_latest_ci_failure.sh`: downloads the latest failed GitHub Actions run metadata and failed logs into `artifacts/ci-failures/latest/` via `gh`.
- `tools/ci/test_stability_guards.sh`: polling/unstable test pattern guard.
- `tools/ci/solution_split_guards.sh`: split build guard.
- `tools/ci/test_solution_ownership_guard.sh`: verifies every `test/*.csproj` is owned by `aevatar.slnx` or the single slow-test project.
- `tools/ci/projection_route_mapping_guard.sh`: projection reducer routing static guard.
- `tools/ci/restore_and_build.sh`: shared restore/build entry used by CI jobs.
- `tools/ci/event_sourcing_regression.sh`: EventSourcing regression entry (core tests + Orleans/Garnet persistence smoke). Architecture guards run once via the `fast-gates` CI job.

## Integration/Smoke Scripts

- `tools/ci/projection_provider_e2e_smoke.sh`
  - Starts Elasticsearch + Neo4j from `docker-compose.projection-providers.yml`.
  - Waits for readiness, runs all `Category=ProviderIntegration` projects, and cleans up containers.
  - Persists and prints the real-Elasticsearch workflow report write-cost table at
    `artifacts/ci/workflow-report-artifact-write-cost.txt`.
- `tools/ci/orleans_garnet_persistence_smoke.sh`: Orleans + Garnet persistence smoke.

## Workflow Mapping

- `.github/workflows/ci.yml`
  - Shared runner preparation is centralized in local action:
    - `.github/actions/prepare-runner/action.yml` (`setup-dotnet` + NuGet cache + optional `ripgrep` install)
  - Job `changes`
    - Uses path filters to detect whether projection-provider or Kafka-runtime integration jobs must run.
  - Job `fkst-host-policy`
    - Runs the host FKST policy gate for PR updates; PR-side comment/review automation is handled by the `github-devloop-pr` package in FKST supervise.
  - Job `fast-gates`
    - Runs static architecture and test-stability guards.
  - Test authority
    - Job `coverage-quality` runs restore/build + `tools/ci/coverage_quality_guard.sh`, which validates test ownership and runs full `dotnet test aevatar.slnx` with coverage.
    - Job `slow-test-guards` runs `tools/ci/slow_test_guards.sh` for the independent slow-test project.
    - `.slnf` files are build-boundary inputs only; tests are not executed through split solution filters.
  - Job `projection-provider-e2e`
    - Runs `tools/ci/projection_provider_e2e_smoke.sh`.
    - Uploads the workflow report-artifact write-cost table as a CI artifact.
    - Triggered on projection-provider related changes, `main/dev` pushes, or manual dispatch.
  - Job `kafka-transport-integration`
    - Starts Kafka and runs the distributed runtime integration test.
    - Triggered on runtime integration related changes, `main/dev` pushes, or manual dispatch.
  - Job `event-sourcing-regression`
    - Runs `tools/ci/event_sourcing_regression.sh`.
    - Triggered on EventSourcing/runtime related changes, `main/dev` pushes, or manual dispatch.
  - Job `coverage-quality`
    - Runs restore/build + `tools/ci/coverage_quality_guard.sh`.
    - Uploads `artifacts/coverage/**` as CI artifacts (`coverage-quality-report`).
    - Uploads the filtered `artifacts/coverage/**/report/Cobertura.xml` file to Codecov when `CODECOV_TOKEN` is available, using the same assembly/file filters as the local quality gate; upload failures are non-blocking because the local coverage guard is the authoritative quality gate.
    - Triggered on `main/dev` pushes, nightly schedule, or manual dispatch.
  - Job `distributed-3node-smoke` -> `tools/ci/distributed_3node_smoke.sh`
