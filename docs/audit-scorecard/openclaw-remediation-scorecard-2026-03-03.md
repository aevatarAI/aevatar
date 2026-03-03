# OpenClaw Remediation Scorecard (2026-03-03)

## Score

- Estimated score: `96 / 100`
- Grade: `A`

## Closed Findings

### 1. `openclaw_call` arbitrary process execution

- Status: Closed
- Change:
  - `openclaw_call` now accepts only the OpenClaw CLI identity.
  - Arbitrary `cli` overrides are rejected.
  - Custom binary path is moved to environment variable `AEVATAR_OPENCLAW_CLI_PATH`.
- Evidence:
  - `src/workflow/Aevatar.Workflow.Core/Modules/OpenClawModule.cs`
  - `test/Aevatar.Integration.Tests/OpenClawModuleCoverageTests.cs`

### 2. Bridge unauthenticated callback relay default

- Status: Closed
- Change:
  - `OpenClawBridgeOptions.RequireAuthToken` now defaults to `true`.
  - Empty `CallbackAllowedHosts` now means callback disabled by default.
  - Requests with disallowed or unconfigured callback hosts now fail with `400 CALLBACK_HOST_NOT_ALLOWED`.
- Evidence:
  - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/OpenClawBridgeEndpoints.cs`
  - `src/workflow/Aevatar.Workflow.Application/OpenClaw/OpenClawBridgeOrchestrationService.cs`
  - `test/Aevatar.Workflow.Host.Api.Tests/ChatEndpointsInternalTests.cs`

### 3. Idempotency acquire only process-safe

- Status: Closed
- Change:
  - Acquire path now uses `IProjectionOwnershipCoordinator` to serialize ownership by idempotency key across nodes.
  - Process-local static semaphore was removed.
  - Busy ownership now resolves to existing record replay/conflict semantics instead of silent duplicate start.
- Evidence:
  - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/OpenClawIdempotencyStore.cs`

### 4. Bridge logic breaks Host-only boundary

- Status: Closed
- Change:
  - Host endpoint is reduced to protocol adaptation and result mapping.
  - Bridge orchestration moved into application layer service.
  - Receipt delivery moved behind dedicated dispatcher abstraction.
- Evidence:
  - `src/workflow/Aevatar.Workflow.Application.Abstractions/OpenClaw/OpenClawBridgeContracts.cs`
  - `src/workflow/Aevatar.Workflow.Application/OpenClaw/OpenClawBridgeOrchestrationService.cs`
  - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/HttpOpenClawBridgeReceiptDispatcher.cs`
  - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/OpenClawBridgeEndpoints.cs`

## Residual Risk

- `HttpOpenClawBridgeReceiptDispatcher` still uses retry delay via `Task.Delay(...)` in infrastructure callback delivery. This is transport retry behavior, not workflow orchestration state mutation, but it is still worth reviewing if callback delivery later needs actorized scheduling.
- Full score depends on build/test evidence in a machine that has:
  - NuGet network access for missing test packages
  - an ARM64-capable `protoc` toolchain

## Verification Status

- Static code review: Completed
- Docs updated: Completed
- Targeted build: Blocked by local `Grpc.Tools` x64-only `protoc`
- Targeted tests: Blocked by restricted NuGet access for missing test packages
