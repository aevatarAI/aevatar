# NyxID MCP Dynamic Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete #3025 by making ordinary current-turn connected-service discovery consume the same NyxID MCP operation catalog as workflow authoring, without silently widening dynamic tool exposure.

**Architecture:** Keep `/keys` only for exact instance inventory, management, credential ownership, routing, and execution-time revalidation. Replace dynamic operation discovery with the existing `NyxIdMcpOperationCatalog` boundary over `GET /api/v1/mcp/config`. NyxID's current stable catalog has no explicit dynamic-exposure policy equivalent to `x-aevatar-tool`, so current-turn operation exposure fails closed while workflow admission continues to use the normalized endpoints.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, Protobuf, NyxID HTTP boundary.

## Global Constraints

- Exact `user_service_id` and `endpoint_id` remain opaque, independent identities; never derive either from display name, slug, method, or path.
- Dynamic discovery must not request `/api/v1/proxy/services/{user_service_id}/openapi.json` when MCP config is available.
- Generic proxy and arbitrary caller-authored method/path must not enter the current-turn tool catalog.
- Absence of an explicit typed dynamic-exposure policy fails closed; do not treat workflow admission as current-turn exposure authorization.
- Fixed exact-service inventory/update/route/delete tools keep their existing `/keys` authority and approval behavior.
- No process-local catalog, token, service, endpoint, or caller cache.
- External JSON is parsed only at the existing NyxID adapter boundary; no second MCP DTO/parser/client.
- Tests use distinct identities such as `user_service_id = "usvc-alpha"` and `endpoint_id = "endpoint-alpha"`.

---

### Task 1: Route current-turn discovery through the shared MCP catalog

**Files:**
- Modify: `test/Aevatar.AI.Tests/NyxIdConnectedServiceToolSourceTests.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdConnectedServiceToolSource.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/NyxIdServiceTools.cs`

**Interfaces:**
- Consumes: `NyxIdApiClient.GetMcpConfigAsync(string token, CancellationToken ct)` and `NyxIdMcpOperationCatalog.Parse(...)`.
- Produces: request-local exact management tools plus bounded structured logs derived from `ExternalCapabilityDiscoveryDiagnosticCode`; no dynamic operation tool until NyxID publishes an explicit typed exposure policy.

- [x] Add a failing source test whose fake NyxID exposes an exact, non-generic MCP endpoint and a raw OpenAPI document. Assert one `/keys` read, one `/mcp/config` read, zero raw OpenAPI reads, four fixed tool names, no `nyxid_service_request`, and no operation tool.
- [x] Run the focused test and confirm RED because the current source requests raw OpenAPI and exposes both arbitrary request and marked operation tools.
- [x] Inject the existing `NyxIdApiClient` into `NyxIdConnectedServiceToolSource`, parse MCP config with `NyxIdMcpOperationCatalog`, and log only bounded diagnostic code/count values.
- [x] Change `NyxIdServiceTools.Create` to return inventory, update, route, and delete only.
- [x] Add tests for MCP source failure, caller cancellation, generic-only catalog, and exact endpoint identity; fixed management tools remain available when only MCP operation discovery is unavailable.
- [x] Run the focused source tests and confirm GREEN.

### Task 2: Delete the obsolete raw OpenAPI and arbitrary request path

**Files:**
- Delete: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/AevatarToolMarker.cs`
- Delete: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/ConnectedServiceOperationTool.cs`
- Delete: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/ConnectedServiceToolNaming.cs`
- Delete: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/OpenApiSchemaInliner.cs`
- Delete: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/OpenApiToolSpecParser.cs`
- Delete: `test/Aevatar.AI.Tests/ConnectedServiceToolSpecParserTests.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/ConnectedServiceToolOperation.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/NyxIdServiceInstanceClient.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/nyxid_service_tools.proto`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdServiceToolsTests.cs`

**Interfaces:**
- Retains: `ConnectedServiceToolParameter` and `ParameterLocation` as the shared typed parameter model consumed by MCP parsing and workflow proof construction.
- Removes: raw OpenAPI parsing/fetch, generic operation-name construction, arbitrary service request messages/tool, and their unused execution helpers.

- [x] Delete tests whose only subject is the raw OpenAPI parser or arbitrary method/path request tool.
- [x] Delete raw parser, schema inliner, marker, naming, and disconnected operation executor code.
- [x] Remove `GetSpecAsync` and `GetProxyServiceOpenApiAsync`.
- [x] Remove `nyxid_service_request`, its Protobuf request/result messages, and unused request execution helpers while preserving the workflow header validation used by proof-bound `NyxIdProxyTool`.
- [x] Run `rg` to prove there is no production caller of the deleted raw path and no current-turn arbitrary request tool.
- [x] Run `Aevatar.AI.Tests` and confirm GREEN.

### Task 3: Document and verify the single catalog boundary

**Files:**
- Modify: `docs/canon/nyxid-connected-service-tools.md`
- Modify: `docs/superpowers/plans/2026-07-30-nyxid-mcp-dynamic-discovery.md`

**Interfaces:**
- Documents: NyxID MCP config is the only operation descriptor source; `/keys` remains the exact instance/credential management boundary; dynamic exposure is fail closed until a typed policy exists.

- [x] Update the canonical flow and remove claims that current-turn dynamic operations parse raw OpenAPI or expose `nyxid_service_request`.
- [x] Run focused tests, full build, full test suite, architecture guards, test stability guard, workflow binding boundary guard, query projection priming guard, docs lint, and `git diff --check`.
- [x] Record exact verification results in this plan.

## Verification Record — 2026-07-30

- NyxID contract: `nyxid v0.8.0`; `nyxid whoami` and `nyxid doctor` succeeded. ChronoAIProject/NyxID#1262 is closed and PR #1265 is merged at `02f7188659df7bd70eb73c2a2fb691bd2ba9f03f`. The merged source publishes `contract_version="1.0"`, authoritative `catalog_digest`, opaque endpoint identities, scoped REST/MCP catalog parity, and typed response content/binary facts.
- Focused shared NyxID tests: 151 passed, 0 failed.
- `dotnet build aevatar.slnx --nologo`: succeeded with 0 errors; existing repository warnings remain.
- `dotnet test aevatar.slnx --nologo --no-restore --no-build --logger 'console;verbosity=minimal'`: succeeded with 0 failed tests.
- `bash tools/ci/architecture_guards.sh`: passed.
- `bash tools/ci/test_stability_guards.sh`: passed.
- `bash tools/ci/workflow_binding_boundary_guard.sh`: passed.
- `bash tools/ci/query_projection_priming_guard.sh`: passed.
- `bash tools/docs/lint.sh`: passed, 83 files checked with 0 errors.
- `git diff --check`: passed.
