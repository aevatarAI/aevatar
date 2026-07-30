# Finance Workflow Acceptance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix #3052, #3061, and #3062 and add an actor-owned explicit NyxID request capability so the six finance workflow artifacts can use known APIs without requiring OpenAPI.

**Architecture:** Keep `nyxid_proxy` as the single runtime adapter. `nyxid_operation` is the typed `PublishedEndpoint(endpoint_id)` origin; `nyxid_request` is the typed `AuthoredRequest(request_contract_digest)` origin. An authored request becomes executable only after an authenticated binder explicitly confirms the current digest/risk and the definition actor persists `NyxIdExplicitRequestGrant`. Studio save/apply preserves a selector but never grants it. Both origins project into the same request builder while keeping member, workflow, published-service, and UserService identities distinct.

**Tech Stack:** .NET 10, C#, Protobuf, Orleans, xUnit, FluentAssertions, YamlDotNet.

## Global Constraints

- Preserve Domain/Application/Infrastructure/Host layering and the single actor-owned admission/runtime chain.
- All stable internal contracts are Protobuf; no JSON bag becomes authoritative state.
- `user_service_id`, member identity, workflow identity, and published service identity remain distinct.
- No legacy raw-route fallback and no tenant-specific values in repository fixtures or GitHub issues.
- Runtime authored requests make no MCP/OpenAPI or inventory read: they validate the committed proof/grant and use one exact proxy route with the exact `user_service_id` and server-derived slug constraint.
- Durable execution is only for binder-attested `READ_ONLY` GET/HEAD/OPTIONS plus exact-service durable authorization. Unattested safe methods, POST/PUT/PATCH, and DELETE are approval-required and interactive-only.
- Use TDD for every behavior change and run `test_stability_guards.sh` after test edits.

---

### Task 1: Studio typed capability round-trip (#3062)

**Files:**
- Modify: `src/Aevatar.Studio.Domain/Studio/Compatibility/WorkflowCompatibilityProfile.cs`
- Modify: `src/Aevatar.Studio.Domain/Studio/Models/StepModel.cs`
- Create: `src/Aevatar.Studio.Domain/Studio/Models/StepCapability.cs`
- Modify: `src/Aevatar.Studio.Infrastructure/Serialization/YamlWorkflowDocumentService.cs`
- Test: `test/Aevatar.Studio.Tests/EditorControllerSerializationTests.cs`

**Interfaces:**
- Produces: `StepCapability` with mutually exclusive `NyxIdOperation` and `NyxIdRequest` models, serialized as `capability.nyxid_operation` or `capability.nyxid_request`.

- [ ] Add failing parse/serialize tests proving both selector variants survive YAML -> document JSON -> YAML, including nested `children`, and unknown variants produce a finding.
- [ ] Run the focused tests and confirm failure because `capability` is unknown or missing from output.
- [ ] Add the minimal typed models, allowed step field, recursive parser, and recursive serializer.
- [ ] Re-run focused Studio tests and confirm they pass.

### Task 2: Shared caller credential selection (#3052)

**Files:**
- Create: `src/platform/Aevatar.GAgentService.Hosting/Endpoints/WorkflowCapabilityAdmissionCredentialSelector.cs`
- Modify: `src/platform/Aevatar.GAgentService.Hosting/Endpoints/WorkflowCapabilityAdmissionHttpContext.cs`
- Modify: `src/Aevatar.Studio.Hosting/StudioWorkflowCapabilityAdmissionHttpContext.cs`
- Test: `test/Aevatar.GAgentService.Tests/WorkflowCapabilityAdmissionCredentialSelectorTests.cs`
- Test: `test/Aevatar.Studio.Tests/StudioWorkflowCapabilityAdmissionHttpContextTests.cs`

**Interfaces:**
- Produces: an internal selector result containing normalized bearer or an explicit invalid status. Studio references the existing GAgentService Hosting project and uses the same implementation.

- [ ] Add failing matrices for bearer-only, delegation-only, bearer precedence, malformed Authorization with delegation, duplicate headers, and missing credentials.
- [ ] Run focused tests and confirm delegation-only admission currently has no caller token.
- [ ] Implement the single header selector and make both admission contexts fail closed on invalid input.
- [ ] Re-run focused tests and confirm the matrices pass.

### Task 3: Channel Orleans scheduler preservation (#3061)

**Files:**
- Modify: `src/Aevatar.AI.ToolProviders.AevatarInvocation/AevatarInvocationDispatcher.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/WorkflowRunDelivery/WorkflowRunBackgroundDeliveryRegistrationPort.cs`
- Test: `test/Aevatar.Integration.Tests/NyxIdChatWorkflowDeliveryOrleansIntegrationTests.cs`

**Interfaces:**
- Preserves: existing `IWorkflowRunBackgroundDeliveryRegistrationPort` contract; only continuation scheduling changes.

- [ ] Add an Orleans integration test whose reservation dependency completes asynchronously and whose next actor-runtime access must remain on the activation scheduler.
- [ ] Run the test and confirm the activation-access violation.
- [ ] Remove only activation-sensitive `ConfigureAwait(false)` calls from reservation, dispatch, registration, and abandonment continuations.
- [ ] Re-run the integration test and related NyxID chat tests.

### Task 4: Typed explicit NyxID request authoring and proof

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Abstractions/workflow_capability_admission.proto`
- Modify: `src/workflow/Aevatar.Workflow.Abstractions/WorkflowCapabilityAdmissionPlanIntegrity.cs`
- Modify: `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowParser.cs`
- Modify: `src/workflow/Aevatar.Workflow.Core/WorkflowAuthorizationDependencyEvaluator.cs`
- Create: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdExplicitWorkflowCapabilitySource.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ServiceCollectionExtensions.cs`
- Modify: `src/workflow/Aevatar.Workflow.Application/ExternalCapabilities/ExternalWorkflowCapabilityReadinessService.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/WorkflowAuthorizationDependenciesTests.cs`
- Test: `test/Aevatar.Workflow.Application.Tests/WorkflowExternalCapabilityAdmissionServiceTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdExplicitWorkflowCapabilitySourceTests.cs`

**Interfaces:**
- Produces: `NyxIdRequestSelector`, `NyxIdUserRequestCapabilityRef`, and binder-owned `NyxIdExplicitRequestGrant`; source kind `NYX_ID_USER_SERVICES`; all are call-site scoped and digest-covered.
- Consumes: live exact service inventory and existing durable authorization catalog.

- [ ] Add failing parser/integrity tests for the canonical YAML and invalid methods, unsafe templates, duplicate parameter names, sensitive headers, file/body conflicts, and selector/proof mismatch.
- [ ] Add failing source tests proving exact active service resolution without calling MCP config and typed failures for missing/ambiguous/inactive services.
- [ ] Run focused tests and confirm failures for missing contracts/source.
- [ ] Add the minimal Protobuf selectors/proofs/grant, parser mapping, authoring validation, source implementation, source evidence rules, and DI registration. Apply/save must not synthesize a grant.
- [ ] Re-run focused Core/Application/AI tests and confirm they pass.

### Task 5: Project explicit proofs into the existing runtime

**Files:**
- Modify: `src/Aevatar.AI.Abstractions/ToolProviders/AgentToolOperationAdmission.cs`
- Modify: `src/workflow/Aevatar.Workflow.Integration.AI/WorkflowOperationAdmissionToolContextMapper.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdAdmittedRequestBuilder.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdProxyTool.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/Modules/ToolCallModuleContextTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdProxyToolAdmittedOperationTests.cs`

**Interfaces:**
- Produces: provider-neutral `AgentToolOperationAuthorizationBasis.PublishedContract|ExplicitRequest`.
- Reuses: one `NyxIdAdmittedRequestBuilder`, proxy client, binary ingress, and runtime drift boundary.

- [ ] Add failing tests proving explicit GET text/file requests dispatch with no runtime MCP/OpenAPI/inventory read, route fields remain rejected, the exact proxy route uses committed authority, and POST/PUT/PATCH/DELETE durable execution is rejected even with a grant.
- [ ] Run focused tests and confirm explicit proofs are not mapped/executable.
- [ ] Map explicit proofs, support declared scalar query/header parameters and JSON body mode, validate committed proof/grant by authorization basis without a runtime source read, and preserve all existing file bounds and managed artifact ingress.
- [ ] Re-run focused runtime tests and confirm both published and explicit proof paths pass.

### Task 6: Canon and sanitized finance acceptance fixtures

**Files:**
- Modify: `docs/canon/workflow-primitives.md`
- Modify: `docs/canon/workflow-runtime.md`
- Modify: `docs/canon/nyxid-connected-service-tools.md`
- Create: `test/Aevatar.Workflow.Host.Api.Tests/Fixtures/finance-budget-nosend-explicit-request.yaml`
- Create: `test/Aevatar.Workflow.Host.Api.Tests/Fixtures/finance-invoice-file-artifact-explicit-request.yaml`
- Test: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowDefinitionParserExternalCapabilityTests.cs`

**Interfaces:**
- Documents and exercises the exact public YAML contract without production identifiers or side effects.

- [ ] Add failing fixture tests that parse, admit through an explicit binder grant, bind, and preserve explicit request selectors plus declared runtime path slots for budget read and invoice file download shapes.
- [ ] Add the sanitized fixtures and canonical documentation.
- [ ] Run Host API fixture tests and docs lint.

### Task 7: Verification, issue updates, and integration push

**Files:** all files changed above.

- [ ] Run focused affected projects, `test_stability_guards.sh`, `workflow_binding_boundary_guard.sh`, and `architecture_guards.sh`.
- [ ] Run `dotnet build aevatar.slnx --nologo` and `dotnet test aevatar.slnx --nologo`; record exact results.
- [ ] Fetch `origin/feature/integrate`, rebase the isolated branch, and rerun focused tests plus build if the base changed.
- [ ] Commit with an imperative single-purpose message and push the verified HEAD to `origin/feature/integrate` using an explicit refspec.
- [ ] Update all linked issues with commit, affected paths, verification commands, and remaining production rollout status.
- [ ] When mainnet runs the new commit, execute only no-side-effect acceptance first through `nyxid proxy request aevatar`; defer Lark send and approval creation until finance confirms.
