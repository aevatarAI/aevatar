# NyxID MCP Workflow Catalog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement #3024 so `/api/chat` can discover and admit exact NyxID MCP endpoints without losing the trusted owner scope or reparsing raw OpenAPI.

**Architecture:** Preserve the existing workflow admission/proof/runtime enforcement trunk. Fix the two caller-context mappings, replace only the workflow capability source with a typed `/api/v1/mcp/config` adapter, and migrate the workflow selector/proof identity from OpenAPI `operation_id` to NyxID MCP `endpoint_id`. Current-turn dynamic connected-service tools remain unchanged under #3025.

**Tech Stack:** .NET 10, C# 13, Protobuf, xUnit, FluentAssertions, NyxID REST `/api/v1/mcp/config`.

## Global Constraints

- Do not modify NyxID; contract enhancements are tracked by ChronoAIProject/NyxID#1262.
- `owner_scope_id`, NyxID caller identity, `user_service_id`, and `endpoint_id` remain distinct identities.
- Fill caller scope fields independently only when empty; never overwrite an existing owner scope.
- Workflow capability discovery accepts only exact `is_user_service=true`, `is_generic_proxy=false` services.
- Stable workflow identity uses `user_service_id + endpoint_id`; no field may carry both OpenAPI `operationId` and MCP endpoint identity.
- Aevatar generates and persists only its canonical call-site proof; it does not persist a second UserService/OpenAPI catalog.
- Runtime uses committed proof and never performs discovery or query-time priming.
- Generic proxy, duplicate/missing identities, unsupported schema, required sensitive headers, and unverifiable binary response semantics fail closed.
- Durable NyxID credentials remain restricted by exact `allowed_service_ids`.
- Do not change ordinary current-turn connected-service dynamic tool discovery in this issue.

---

### Task 1: Propagate the trusted owner scope

**Files:**
- Modify: `test/Aevatar.Workflow.Core.Tests/Modules/WorkflowRoleGAgentMappingTests.cs`
- Modify: `test/Aevatar.Workflow.Core.Tests/Modules/AgentWorkflowToolSourceAdapterTests.cs`
- Modify: `src/workflow/Aevatar.Workflow.Integration.AI/WorkflowRoleGAgent.cs`
- Modify: `src/workflow/Aevatar.Workflow.Integration.AI/AgentWorkflowToolSourceAdapter.cs`

**Interfaces:**
- Consumes: `WorkflowLlmExecutionIntent.ScopeId` and `WorkflowToolExecutionRequest.ScopeId`.
- Produces: `AgentToolCallerContext.OwnerScopeId` populated from the trusted run scope only when empty.

- [x] Add role-path tests asserting `scope-owner-alpha` reaches both `ScopeId` and `OwnerScopeId`, while a pre-existing different owner scope remains unchanged.
- [x] Run the role-path tests and confirm the missing `OwnerScopeId` assertion fails.
- [x] Add workflow-tool adapter tests asserting the same empty-fill behavior.
- [x] Run the adapter tests and confirm the missing `OwnerScopeId` assertion fails.
- [x] Implement independent empty-fill mapping in both production paths.
- [x] Re-run both test classes and confirm they pass.

### Task 2: Give MCP endpoint identity a single typed meaning

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Abstractions/workflow_capability_admission.proto`
- Modify: `src/workflow/Aevatar.Workflow.Abstractions/WorkflowCapabilityAdmissionPlanIntegrity.cs`
- Modify: `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowParser.cs`
- Modify: `src/workflow/Aevatar.Workflow.Core/WorkflowAuthorizationDependencyEvaluator.cs`
- Modify: `src/workflow/Aevatar.Workflow.Application/ExternalCapabilities/ExternalWorkflowCapabilityReadinessService.cs`
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatRunStartErrorMapper.cs`
- Modify: `src/workflow/Aevatar.Workflow.Integration.AI/WorkflowOperationAdmissionToolContextMapper.cs`
- Modify: affected workflow, binding, and NyxID tests/fixtures.

**Interfaces:**
- Produces: `NyxIdOperationSelector.endpoint_id` and `NyxIdUserServiceCapabilityRef.endpoint_id`.
- Preserves: provider-neutral `AgentToolOperationAdmission.OperationId` only as an execution-proof operation identity at the AI boundary; its value comes from typed `endpoint_id`.

- [x] Change focused parser/integrity tests to author and assert `endpoint_id`.
- [x] Run them and confirm generated proto/API mismatches fail compilation or assertions.
- [x] Rename the proto fields without preserving a hidden `operation_id` compatibility field; bump the admission schema version and explicitly reject earlier persisted plans during revalidation.
- [x] Update all compiler, integrity, DTO/error mapping, proof, and test fixtures to use endpoint identity.
- [x] Run workflow abstractions/core/application/binding targeted tests and confirm they pass.

### Task 3: Replace raw OpenAPI workflow discovery with MCP config

**Files:**
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`
- Create: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/NyxIdMcpOperationCatalog.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/NyxIdOperationAdmissionProofBuilder.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdExternalWorkflowCapabilitySource.cs`
- Rewrite: `test/Aevatar.AI.Tests/NyxIdExternalWorkflowCapabilitySourceTests.cs` fixtures for MCP config.

**Interfaces:**
- `NyxIdApiClient.GetMcpConfigAsync(string token, CancellationToken ct)` calls exactly `/api/v1/mcp/config`.
- `NyxIdMcpOperationCatalog.Parse(...)` returns typed exact UserService/endpoint descriptors plus typed parse/filter diagnostics and one source stamp.
- `NyxIdExternalWorkflowCapabilitySource` builds descriptors and proof from the typed catalog and never calls exact-service raw OpenAPI.

- [x] Add an MCP-config success fixture using distinct `scope-owner-alpha`, `nyx-user-alpha`, `usvc-alpha`, and `endpoint-alpha`; assert list and inspect use `endpoint-alpha`.
- [x] Assert the HTTP handler records `/api/v1/mcp/config` and fails if `/proxy/services/*/openapi.json` is requested.
- [x] Run the tests and confirm they fail against the raw OpenAPI implementation.
- [x] Add malformed/duplicate service and endpoint identity tests, platform/generic filter tests, and unsupported schema/header/body/response tests.
- [x] Implement the smallest typed parser/adapter that maps the published MCP fields and emits deterministic canonical contracts.
- [x] Reuse the existing admission proof schema converter and execution policy derivation; do not create a second proof model.
- [x] Return typed readiness blockers for source unavailable, invisible/filtered selection, ambiguous identity, unsupported request contract, and unverifiable binary/file response.
- [x] Run all NyxID capability-source and proxy proof tests and confirm they pass.

### Task 4: Document and verify the boundary

**Files:**
- Modify: `docs/canon/nyxid-connected-service-tools.md`
- Modify: `docs/canon/workflow-primitives.md`
- Modify: relevant workflow external-capability architecture document discovered during implementation.

**Interfaces:**
- Documents: NyxID owns normalized MCP operation facts; Aevatar owns workflow selector, admission proof, approval, and durable authorization.

- [x] Update canonical docs and Mermaid diagrams to show `/api/v1/mcp/config -> typed boundary -> definition proof -> run actor -> dispatch`.
- [x] Search the workflow capability area for stale author-facing `operation_id` and raw OpenAPI-source claims; remove them where they refer to NyxID MCP endpoint identity.
- [x] Run targeted test projects.
- [x] Run `dotnet build aevatar.slnx --nologo`.
- [x] Run `bash tools/ci/architecture_guards.sh`.
- [x] Run `bash tools/ci/test_stability_guards.sh`.
- [x] Run `bash tools/ci/workflow_binding_boundary_guard.sh`.
- [x] Run `bash tools/ci/query_projection_priming_guard.sh`.
- [x] Run `bash tools/docs/lint.sh`.
- [x] Review the diff against every #3024 acceptance criterion.

## Verification record (2026-07-29)

- `dotnet build aevatar.slnx --nologo`: exit 0, 0 errors.
- `dotnet test aevatar.slnx --nologo`: exit 0, 0 failures; environment-dependent integrations remained explicitly skipped by their existing conditions.
- `bash tools/ci/architecture_guards.sh`: passed.
- `bash tools/ci/test_stability_guards.sh`: passed.
- `bash tools/ci/workflow_binding_boundary_guard.sh`: passed.
- `bash tools/ci/query_projection_priming_guard.sh`: passed.
- `bash tools/docs/lint.sh`: passed, 83 files checked with 0 errors.
- `git diff --check`: passed.
- Static caller audit: workflow discovery is the only `GetMcpConfigAsync` caller; proof-bound runtime does not discover; exact-service raw OpenAPI remains only in the ordinary current-turn dynamic path tracked by #3025.
- Online `/api/chat` smoke verification is intentionally post-deploy: the user required the online environment, and the current online deployment cannot exercise this unmerged change.

### Task 5: Compose capability tools into Mainnet workflow authoring

**Files:**
- Modify: `test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`

**Interfaces:**
- Consumes: existing `AevatarAIFeatureOptions.EnableBindingTools` and `BindingAgentToolSource`.
- Produces: Mainnet workflow role turns can resolve `list_external_workflow_capabilities` and `inspect_external_workflow_capability_readiness` from the existing binding tool source.

- [x] Add a Mainnet composition test asserting `BindingAgentToolSource` is registered as an `IAgentToolSource`.
- [x] Run the focused test and confirm it fails because Mainnet never enables binding tools.
- [x] Enable binding tools only in `ConfigureMainnetAIFeatures`.
- [x] Re-run the focused test and relevant workflow/binding tests.
- [x] Run required guards, solution verification, and `git diff --check`.

Production reproduction (2026-07-30): `workflow=studio`, command `issue-3024-studio-canary-20260730-a`, run `workflow-definition:studio:run:cfd6b857583044d9b9828cc5af141d1c` completed with typed diagnostic `capability_tool_unavailable`; no external operation or mutation was executed.

## Verification record (2026-07-30)

- Regression test RED: Mainnet had no `BindingAgentToolSource` service descriptor before enabling binding tools.
- Focused Mainnet composition test: passed, 1/1.
- Mainnet composition tests: passed, 46/46.
- Binding tests: passed, 25/25.
- `dotnet build aevatar.slnx --nologo --no-restore`: exit 0, 0 errors; 247 existing warnings.
- `dotnet test aevatar.slnx --nologo --no-restore`: exit 0, 0 failures; environment-dependent integrations remained explicitly skipped by their existing conditions.
- `bash tools/ci/architecture_guards.sh`: passed.
- `bash tools/ci/test_stability_guards.sh`: passed.
- `bash tools/ci/workflow_binding_boundary_guard.sh`: passed.
- `bash tools/ci/query_projection_priming_guard.sh`: passed.
- `bash tools/docs/lint.sh`: passed, 83 files checked with 0 errors.
- `git diff --check`: passed.
- Online `/api/chat` smoke verification remains post-deploy because the current Mainnet image does not contain this composition fix.
