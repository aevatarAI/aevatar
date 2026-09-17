# NyxID Managed Workflow Admission Rollout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close proofless managed-workflow NyxID proxy paths and provide a typed, observable, production-gated v2-to-v3 enforcement rollout for issue #3012.

**Architecture:** Keep the existing `AdmittedOperations() -> definition admission plan -> run state -> tool context -> NyxIdOperationRequestBuilder` trunk. Add the only runtime decision at `NyxIdProxyTool`, project one typed execution-policy submessage into the existing proof/digests, resolve the existing connected-service named tool set per Studio request, and derive the enforcement gate only from actor-scoped current-state read models.

**Tech Stack:** .NET 10, C# 14, protobuf, xUnit, FluentAssertions, OpenTelemetry `Meter`, ASP.NET Core hosted services.

## Global Constraints

- Keep dynamic exposure, workflow definition admission, and runtime authorization as three separate policies.
- Missing `x-aevatar-tool` is denied; operation-level `false` overrides service-level `true`.
- Do not add a second OpenAPI parser, admission digest, projection pipeline, or process-local fact registry.
- Core execution semantics use typed protobuf/records; no metadata bag or string-key control plane.
- `Shadow` is the default and preserves legacy behavior; `Enforce` fails proofless managed calls before downstream work.
- Raw `nyxid_proxy` remains available only to ordinary non-workflow human sessions.
- New/rebound workflow definitions use call-site proof v3; persisted runs are never query-time migrated, replayed, or hot-rewritten.
- Existing workflow approval suspend/resume is the only Aevatar approval continuation.
- Telemetry contains no credentials, bodies, headers, paths, user content, or service/user identifiers.
- Tests use distinct service, operation, run, and call-site identities.

---

### Task 1: Establish a clean baseline and preserve existing marker semantics

**Files:**
- Read: `test/Aevatar.AI.Tests/OpenApiToolSpecParserTests.cs`
- Read: `test/Aevatar.AI.Tests/NyxIdConnectedServiceToolSourceTests.cs`
- Read: `test/Aevatar.Workflow.Application.Tests/WorkflowExternalCapabilityAdmissionServiceTests.cs`

**Interfaces:**
- Consumes: existing `AdmittedOperations()` and v3 admission behavior.
- Produces: baseline evidence; no production change.

- [ ] **Step 1: Run the existing marker/admission characterization tests**

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter "FullyQualifiedName~OpenApiToolSpecParserTests|FullyQualifiedName~NyxIdConnectedServiceToolSourceTests|FullyQualifiedName~NyxIdProxyToolAdmittedOperationTests"
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowExternalCapabilityAdmissionServiceTests
```

Expected: PASS, including missing-marker denial, explicit-false precedence, marked dynamic tools, and persisted-v2 rebind behavior.

- [ ] **Step 2: Record the baseline commit**

Run: `git rev-parse HEAD && git status --short`.

Expected: only the approved plan is untracked/modified; no source changes.

---

### Task 2: Enforce the shared managed-workflow proxy decision

**Files:**
- Modify: `src/Aevatar.AI.Abstractions/ToolProviders/AgentToolExecutionContext.cs`
- Modify: `src/Aevatar.AI.Abstractions/ai_messages.proto`
- Modify: `src/Aevatar.AI.Abstractions/ToolProviders/AgentToolExecutionContextMapper.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdToolOptions.cs`
- Create: `src/Aevatar.AI.ToolProviders.NyxId/Observability/NyxIdProxyAdmissionTelemetry.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdProxyTool.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdAgentToolSource.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdProxyToolAdmittedOperationTests.cs`
- Test: `test/Aevatar.AI.Tests/AIAbstractionsProtoCoverageTests.cs`

**Interfaces:**
- Consumes: `AgentWorkflowRuntimeContext.HasManagedParent`, `OperationAdmission`, and ambient `AgentToolRequestContext`.
- Produces: `NyxIdManagedWorkflowAdmissionMode { Shadow, Enforce }`, typed `AgentToolInvocationSurface`, stable `NYXID_OPERATION_ADMISSION_REQUIRED` result/receipt, and bounded decision telemetry.

- [ ] **Step 1: Write RED tests for the early shared guard**

Add tests that construct a proxy with `Enforce`, push a context containing distinct
`ParentRunId = "run-alpha"`, `ParentStepId = "llm-alpha"`, and no proof, then execute raw arguments for text and file-artifact calls. Assert:

```csharp
result.Should().Contain("NYXID_OPERATION_ADMISSION_REQUIRED");
fakeHandler.Requests.Should().BeEmpty();
fileIngress.Calls.Should().Be(0);
tool.CreateResultReceipt("call-alpha", "nyxid_proxy", arguments, result)!
    .ErrorCode.Should().Be("NYXID_OPERATION_ADMISSION_REQUIRED");
```

Also assert `Shadow` reaches the existing fake downstream path and a non-workflow context remains unchanged.

- [ ] **Step 2: Run the guard tests and observe RED**

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter FullyQualifiedName~NyxIdProxyToolAdmittedOperationTests
```

Expected: FAIL because no runtime mode or admission-required branch exists.

- [ ] **Step 3: Add the minimal typed mode, invocation surface, telemetry, and guard**

Implement:

```csharp
public enum NyxIdManagedWorkflowAdmissionMode { Shadow = 0, Enforce = 1 }
public enum AgentToolInvocationSurface { Unspecified = 0, HumanSession = 1, WorkflowToolCall = 2, WorkflowLlmToolLoop = 3 }
```

At the first line of `NyxIdProxyTool.ExecuteAsync`, classify the typed context, record only bounded enum/bool tags, and return the stable error in `Enforce` before argument parsing, tokens, service reads, ingress, or HTTP. `Shadow` records `would_block` and continues. Map the invocation surface through protobuf so approval continuation/restart preserves it.

- [ ] **Step 4: Run the guard and protobuf tests and observe GREEN**

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter "FullyQualifiedName~NyxIdProxyToolAdmittedOperationTests|FullyQualifiedName~AIAbstractionsProtoCoverageTests"
```

Expected: PASS; managed proofless Enforce calls produce zero downstream interactions.

- [ ] **Step 5: Commit the shared boundary**

```bash
git add src/Aevatar.AI.Abstractions src/Aevatar.AI.ToolProviders.NyxId src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs test/Aevatar.AI.Tests
git commit -m "Guard proofless managed NyxID proxy calls"
```

---

### Task 3: Carry and enforce typed operation execution policy

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Abstractions/workflow_capability_admission.proto`
- Modify: `src/workflow/Aevatar.Workflow.Abstractions/WorkflowCapabilityAdmissionPlanIntegrity.cs`
- Modify: `src/Aevatar.AI.Abstractions/ToolProviders/AgentToolOperationAdmission.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/ConnectedServiceToolOperation.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/NyxIdOperationAdmissionProofBuilder.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdExternalWorkflowCapabilitySource.cs`
- Modify: `src/workflow/Aevatar.Workflow.Integration.AI/WorkflowOperationAdmissionToolContextMapper.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdProxyTool.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdConnectedServiceToolSourceTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdProxyToolAdmittedOperationTests.cs`
- Test: `test/Aevatar.Workflow.Application.Tests/WorkflowExternalCapabilityAdmissionServiceTests.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/Modules/ToolCallModuleApprovalTests.cs`

**Interfaces:**
- Consumes: canonical parsed operation safety and existing admission digest.
- Produces: one typed `NyxIdOperationExecutionPolicy` proof field and provider-neutral `AgentToolOperationExecutionPolicy` runtime mapping.

- [ ] **Step 1: Write RED proof-policy and digest tests**

Use literal fixtures with distinct IDs:

```csharp
proof.ExecutionPolicy.Risk.Should().Be(NyxIdOperationRisk.Write);
proof.ExecutionPolicy.Approval.Should().Be(NyxIdOperationApproval.Required);
proof.ExecutionPolicy.EnforcementOwner.Should().Be(NyxIdOperationEnforcementOwner.Aevatar);
proof.ExecutionPolicy.AllowedExecutionModes.Should().Equal(ExternalCapabilityExecutionMode.Interactive);
```

Cover GET, POST, and DELETE; assert changing read-only/destructive/approval semantics changes the existing contract/admission digest. Assert no locally parsed OpenAPI field can set enforcement owner to NyxID.

- [ ] **Step 2: Write RED durable and approval-continuation tests**

Assert durable write/destructive readiness returns a typed blocker and
`UseInteractiveExecution`, while interactive write creates a proof that causes
`NyxIdProxyTool.GetCallSafety` to require approval. Exercise the existing workflow
middleware yield/resume path and assert no request dispatches before the matching grant.

- [ ] **Step 3: Run the focused tests and observe RED**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter "FullyQualifiedName~NyxIdConnectedServiceToolSourceTests|FullyQualifiedName~NyxIdProxyToolAdmittedOperationTests"
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowExternalCapabilityAdmissionServiceTests
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo \
  --filter FullyQualifiedName~ToolCallModuleApprovalTests
```

Expected: FAIL because the proof lacks policy and the proxy is `NeverRequire`.

- [ ] **Step 4: Add the protobuf policy and conservative derivation**

Add enums/submessage for risk, approval, enforcement owner, and repeated allowed
execution modes at field 10 of `NyxIdUserServiceCapabilityRef`. Derive policy from
the already parsed method/marker. Add it to `CanonicalContract()`; protobuf inclusion
automatically updates the existing admission digest.

- [ ] **Step 5: Bind policy to readiness and runtime approval**

Reject durable policies that omit `DURABLE` during readiness. Map the proof to the
AI context. Make raw proxy `ApprovalMode.Auto`; return exact call safety only when a
valid proof policy exists, leaving ordinary raw human calls on the existing NyxID-owned
behavior. In `Enforce`, reject a managed proof whose typed policy is absent/invalid.

- [ ] **Step 6: Run the focused tests and observe GREEN**

Run the commands from Step 3.

Expected: PASS, including write approval suspension/resume and durable rejection.

- [ ] **Step 7: Commit typed policy**

```bash
git add src/workflow/Aevatar.Workflow.Abstractions src/workflow/Aevatar.Workflow.Integration.AI \
  src/Aevatar.AI.Abstractions src/Aevatar.AI.ToolProviders.NyxId \
  test/Aevatar.AI.Tests test/Aevatar.Workflow.Application.Tests test/Aevatar.Workflow.Core.Tests
git commit -m "Carry NyxID execution policy in admission proofs"
```

---

### Task 4: Mount admitted connected-service tools into the Studio turn

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowAgentToolScopeDefinition.cs`
- Modify: `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowParser.cs`
- Modify: `src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs`
- Modify: `src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto`
- Modify: `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs`
- Modify: `src/Aevatar.AI.ToolProviders.ToolSetRegistry/IToolSetRegistry.cs`
- Modify: `src/Aevatar.AI.ToolProviders.ToolSetRegistry/ToolSetRegistry.cs`
- Modify: `src/workflow/Aevatar.Workflow.Integration.AI/Aevatar.Workflow.Integration.AI.csproj`
- Modify: `src/workflow/Aevatar.Workflow.Integration.AI/WorkflowRoleGAgent.cs`
- Modify: `src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionCatalog.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/Primitives/WorkflowParserConfigurationTests.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/Modules/WorkflowRuntimeModuleBranchTests.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/Modules/WorkflowRoleGAgentMappingTests.cs`
- Test: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowDefinitionCatalogTests.cs`

**Interfaces:**
- Consumes: `IToolSetRegistry`, `ToolSetNames.NyxIdConnectedServices`, caller-scoped `NyxIdConnectedServiceToolSource`.
- Produces: typed role/step `tool_set_refs` propagated to each LLM intent and a request-local `AgentTurnToolCatalog` containing exact discovered tools.

- [ ] **Step 1: Write RED parser and scope-propagation tests**

Parse a role with `tool_sets: [nyxid.connected_services]`; assert the typed role
scope, role/step intersection, and `WorkflowLlmExecutionIntent.AgentToolScope`
round-trip preserve the ref without encoding it as an allowed tool name.

- [ ] **Step 2: Write RED Studio request-time parity tests**

Initialize a workflow role with a recording registry/source whose discovery reads the
ambient caller token. Invoke one turn and assert the provider request contains the
marked per-operation tool, excludes an unmarked operation, and never contains raw
`nyxid_proxy`. Invoke a second caller token and assert discovery runs again with no
cross-caller catalog reuse.

- [ ] **Step 3: Run the focused tests and observe RED**

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo \
  --filter "FullyQualifiedName~WorkflowParserConfigurationTests|FullyQualifiedName~WorkflowRuntimeModuleBranchTests|FullyQualifiedName~WorkflowRoleGAgentMappingTests"
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowDefinitionCatalogTests
```

Expected: FAIL because workflow scopes carry only static tool names and Studio still allows raw proxy.

- [ ] **Step 4: Implement typed request-time tool-set resolution**

Add `tool_set_refs` to the existing scope contract and propagate it through the run
actor. Add a string-name `IToolSetRegistry.Resolve` overload. In
`WorkflowRoleGAgent`, under the current typed tool context, resolve and discover each
source for every request, construct one `AgentTurnToolCatalog`, and union only
those exact dynamic names into the current turn visibility. Unknown sets, discovery
failure, and name collision fail closed for those tools without mutating actor/global
registries.

- [ ] **Step 5: Update the built-in Studio contract**

Add `tool_sets: [nyxid.connected_services]`; remove `nyxid_proxy` from
`allowed_tools`; replace prompt instructions for current-turn calls with admitted
per-operation tools while retaining selector/readiness instructions for workflow
authoring.

- [ ] **Step 6: Run the focused tests and observe GREEN**

Run the commands from Step 3.

Expected: PASS; dynamic tools are caller-local and raw proxy is absent from Studio.

- [ ] **Step 7: Commit Studio tool resolution**

```bash
git add src/Aevatar.AI.ToolProviders.ToolSetRegistry src/workflow test/Aevatar.Workflow.Core.Tests test/Aevatar.Workflow.Host.Api.Tests
git commit -m "Use admitted NyxID tools in Studio turns"
```

---

### Task 5: Gate Enforce on actor-scoped v2 inventory

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Projection/workflow_projection_transport.proto`
- Modify: `src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionCurrentStateProjector.cs`
- Create: `src/Aevatar.Mainnet.Host.Api/WorkflowAdmission/NyxIdWorkflowAdmissionEnforcementStartupGuard.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`
- Test: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowExecutionProjectionProjectorTests.cs`
- Create: `test/Aevatar.Capabilities.Tests/NyxIdWorkflowAdmissionEnforcementStartupGuardTests.cs`
- Test: `test/Aevatar.GAgentService.Tests/Infrastructure/ServiceImplementationAdaptersTests.cs`

**Interfaces:**
- Consumes: `WorkflowActorBindingDocument.CapabilityAdmissionPlan`, actor-owned `WorkflowRunState.CapabilityAdmissionPlan`, `WorkflowExecutionCurrentStateDocument.Status`, and existing online service revision prepare/activate paths.
- Produces: a startup release gate for `Enforce` that scans projection pages by cursor and refuses legacy/invalid serving definitions or active/paused legacy runs.

- [ ] **Step 1: Write RED projection and gate tests**

Assert the run current-state document includes the actor-owned admission plan. Feed the
guard paginated documents containing distinct `wf-v2`, `wf-v3`, `run-v2-active`,
`run-v2-complete`, and invalid-policy identities. Assert:

```csharp
await act.Should().ThrowAsync<InvalidOperationException>()
    .WithMessage("*CAPABILITY_ADMISSION_REBIND_REQUIRED*");
```

Shadow performs no scan; Enforce allows only when every serving definition is v3 with
typed policy and every v2 run is terminal/isolated.

- [ ] **Step 2: Run the tests and observe RED**

```bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowExecutionProjectionProjectorTests
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo \
  --filter FullyQualifiedName~NyxIdWorkflowAdmissionEnforcementStartupGuardTests
```

Expected: FAIL because run read models omit the plan and no startup gate exists.

- [ ] **Step 3: Project the plan and implement the paginated read-only gate**

Copy the plan from committed `WorkflowRunState` into the actor-scoped current-state
document. In the hosted guard, query bindings and current states with deterministic
sorts and `NextCursor` until exhausted; retain only bounded counts/sample IDs in the
exception. Never activate, prime, replay, or mutate a projection in the query path.

- [ ] **Step 4: Prove the existing online rebind path remains the migration path**

Extend `WorkflowServiceImplementationAdapter` tests to show a v2 persisted artifact
returns rebind-required and that resubmitting the exact workflow without the old plan
invokes live admission to create a v3 revision artifact. Do not add another migration command.

- [ ] **Step 5: Run the tests and observe GREEN**

Run the commands from Step 2 plus:

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo \
  --filter FullyQualifiedName~ServiceImplementationAdaptersTests
```

Expected: PASS; Enforce cannot start on live legacy inventory and rebind uses the existing revision path.

- [ ] **Step 6: Commit the release gate**

```bash
git add src/workflow/Aevatar.Workflow.Projection src/Aevatar.Mainnet.Host.Api \
  test/Aevatar.Workflow.Host.Api.Tests test/Aevatar.Capabilities.Tests test/Aevatar.GAgentService.Tests
git commit -m "Gate NyxID admission enforcement on v2 inventory"
```

---

### Task 6: Lock canonical semantics and complete verification

**Files:**
- Modify: `docs/canon/nyxid-connected-service-tools.md`
- Modify: `docs/canon/workflow-runtime.md`
- Create: `docs/operations/2026-07-28-nyxid-workflow-admission-rollout.md`
- Modify as required: focused tests from Tasks 2-5.

**Interfaces:**
- Consumes: implemented mode, metric name/tags, startup gate, revision endpoints.
- Produces: canonical policy separation and an exact Shadow -> rebind/drain -> canary -> Enforce -> Shadow rollback runbook.

- [ ] **Step 1: Update canonical docs and the operator runbook**

Document exact configuration, bounded telemetry dimensions, inventory/gate behavior,
online revision preparation/activation, schedule repointing, v2 drain/legacy-worker
isolation, canary matrix, and same-binary rollback. Do not include credentials, full
paths, or request content in example telemetry.

- [ ] **Step 2: Search for stale product semantics**

Run:

```bash
rg -n "Use .*nyxid_proxy.*current-turn|prefer a workflow runtime call through .*nyxid_proxy|allowed_tools" \
  src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionCatalog.cs \
  docs/canon docs/operations test/Aevatar.Workflow.Host.Api.Tests/WorkflowDefinitionCatalogTests.cs
```

Expected: no Studio instruction exposes raw proxy for model-authored current-turn calls.

- [ ] **Step 3: Run all affected tests and mandatory guards**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

Expected: every command exits 0.

- [ ] **Step 4: Run full build and test verification**

```bash
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
```

Expected: build succeeds and full suite reports zero failures.

- [ ] **Step 5: Commit documentation and any verification-only corrections**

```bash
git add docs/canon docs/operations test src
git diff --cached --check
git commit -m "Document NyxID workflow admission operations"
```

- [ ] **Step 6: Rebase, re-verify, and fast-forward the requested target**

```bash
git fetch origin feature/integrate
git rebase origin/feature/integrate
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
bash tools/ci/architecture_guards.sh
git push origin HEAD:feature/integrate
git ls-remote --heads origin feature/integrate
```

Expected: push succeeds without force; the remote SHA equals local `HEAD`.
