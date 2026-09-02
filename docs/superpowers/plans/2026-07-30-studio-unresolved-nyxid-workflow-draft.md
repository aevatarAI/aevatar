# Studio Unresolved NyxID Workflow Draft Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Let /api/chat create an editable Team workflow member and scope-owned workflow draft when no exact NyxID operation descriptor exists, while bind, schedule, and runtime admission stay fail closed.

**Architecture:** AppScopedWorkflowService validates complete YAML with the existing runtime parser and saves it without live capability admission. A narrow Studio Application port composes the existing member and actor-backed workspace paths. One Studio agent tool exposes that use case; runnable provisioning is unchanged.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, existing Studio member/workspace ports, existing workflow runtime parser.

## Global Constraints

- Keep memberId, workflowId, and publishedServiceId distinct.
- Return /scopes/:scopeId/teams/:teamId/members/:memberId/workflow?workflowId=:draftWorkflowId.
- Search/inference is authoring evidence only; never invent NyxID identities, route authority, or proof.
- Add no generic proxy, raw OpenAPI parser, provider-specific X adapter, bind, schedule, run, or publish action to the draft path.
- Report workspace command acceptance and projection-pending readiness honestly.
- Reuse the runnable provisioner's deterministic (scope, team, display name) identity.
- Use TDD and run the repository test-stability guard after changing tests.

---

### Task 1: Separate editor draft validation from runtime admission

**Files:**
- Modify: src/Aevatar.Studio.Application/Studio/Contracts/WorkspaceContracts.cs
- Modify: src/Aevatar.Studio.Application/AppScopedWorkflowService.cs
- Modify: src/Aevatar.Studio.Hosting/Controllers/WorkspaceController.cs
- Modify: src/Aevatar.Studio.Hosting/StudioHostingServiceCollectionExtensions.cs
- Modify: test/Aevatar.Studio.Tests/AppScopedWorkflowServiceDeleteDraftTests.cs
- Modify: test/Aevatar.Studio.Tests/WorkspaceControllerWorkflowDraftCreateTests.cs
- Modify: test/Aevatar.Studio.Tests/StudioWorkflowCapabilityAdmissionContractTests.cs

**Interfaces:**
- Consumes: IWorkflowDefinitionParser.ParseWorkflowYamlAsync and existing workspace query/command ports.
- Produces: AppScopedWorkflowService.SaveDraftAsync(scopeId, workflowId, request, ct), returning WorkflowDraftCreateAcceptedResponse.

- [ ] **Step 1: Write failing boundary tests**

Add tests proving unresolved nyxid_proxy YAML is preserved verbatim, parser rejection occurs before workspace access, a supplied stable draft ID is used, and the response remains accepted/projection_pending. Update contract tests so draft requests carry no transient credential context.

~~~csharp
var accepted = await service.SaveDraftAsync(
    "scope-alpha",
    "wf-alpha",
    new SaveWorkflowDraftRequest("scope:scope-alpha", "X Digest", null, unresolvedYaml));
workspace.SavedDrafts.Single().WorkflowId.Should().Be("wf-alpha");
workspace.SavedDrafts.Single().Yaml.Should().Be(unresolvedYaml.Trim());
accepted.Readiness.Stage.Should().Be("projection_pending");
~~~

- [ ] **Step 2: Verify RED**

~~~bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~AppScopedWorkflowServiceDeleteDraftTests|FullyQualifiedName~WorkspaceControllerWorkflowDraftCreateTests|FullyQualifiedName~StudioWorkflowCapabilityAdmissionContractTests"
~~~

Expected: compilation/assertion failure because the stable save API and parser-only constructor do not exist.

- [ ] **Step 3: Implement the minimum boundary change**

Require IWorkflowDefinitionParser; reject unsuccessful parse results before workspace reads/writes; use the parsed workflow name; preserve submitted YAML without Studio-model serialization. Remove draft admission injection/context. Add stable-id save as an upsert command while UpdateDraftAsync still requires an observed existing draft.

- [ ] **Step 4: Verify GREEN and commit**

Run the Step 2 command, then:

~~~bash
git add src/Aevatar.Studio.Application src/Aevatar.Studio.Hosting test/Aevatar.Studio.Tests
git commit -m "Separate workflow drafts from runtime admission"
~~~

### Task 2: Add deterministic member-and-draft provisioning

**Files:**
- Create: src/Aevatar.Studio.Application.Abstractions/Provisioning/IStudioMemberWorkflowDraftProvisioningPort.cs
- Create: src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowDraftProvisioningService.cs
- Modify: src/Aevatar.Studio.Application/Studio/Services/StudioWorkflowProvisioningService.cs
- Modify: src/Aevatar.Studio.Hosting/StudioHostingServiceCollectionExtensions.cs
- Create: test/Aevatar.Studio.Tests/StudioMemberWorkflowDraftProvisioningServiceTests.cs

**Interfaces:**
- Consumes: IStudioMemberService, IWorkflowDefinitionParser, and AppScopedWorkflowService.
- Produces: IStudioMemberWorkflowDraftProvisioningPort.SaveAsync and typed request/result/error contracts.

- [ ] **Step 1: Write failing orchestration tests**

Use distinct m-alpha, wf-alpha, and svc-alpha fixtures. Prove unresolved YAML creates/reuses only a workflow member shell plus draft command; same ownership tuple yields the same IDs; invalid YAML mutates nothing; wrong Team/kind is typed failure; workspace failure returns the reusable member ID.

~~~csharp
result.Status.Should().Be("draft_save_accepted");
result.Runnable.Should().BeFalse();
result.MemberId.Should().NotBe(result.WorkflowId);
result.Blockers.Should().ContainSingle(x =>
    x.Code == "NYXID_OPERATION_SELECTION_REQUIRED");
~~~

- [ ] **Step 2: Verify RED**

~~~bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter FullyQualifiedName~StudioMemberWorkflowDraftProvisioningServiceTests
~~~

Expected: compilation failure because the new port/contracts/service do not exist.

- [ ] **Step 3: Implement minimal orchestration**

Validate before mutation. Reuse StudioWorkflowProvisioningService.BuildProvisionKey for wf-{key} and workflow-{key}. Validate an explicit/existing member's Team and workflow kind, otherwise create the deterministic shell. Save through AppScopedWorkflowService.SaveDraftAsync. Return NYXID_OPERATION_SELECTION_REQUIRED when a nyxid_proxy invocation has an empty selector; otherwise WORKFLOW_BIND_REQUIRED. Inject no binding, scheduling, run, publication, or proxy port.

- [ ] **Step 4: Verify GREEN and commit**

Run Step 2, then:

~~~bash
git add src/Aevatar.Studio.Application.Abstractions src/Aevatar.Studio.Application src/Aevatar.Studio.Hosting test/Aevatar.Studio.Tests
git commit -m "Add unresolved workflow draft provisioning"
~~~

### Task 3: Expose the Chat draft tool

**Files:**
- Create: src/Aevatar.AI.ToolProviders.StudioProvisioning/CreateStudioMemberWorkflowDraftTool.cs
- Modify: src/Aevatar.AI.ToolProviders.StudioProvisioning/ProvisionWorkflowScheduleToolSource.cs
- Modify: src/Aevatar.AI.ToolProviders.StudioProvisioning/ServiceCollectionExtensions.cs
- Modify: src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs
- Create: test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests/CreateStudioMemberWorkflowDraftToolTests.cs
- Modify: test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests/ProvisionWorkflowScheduleToolTests.cs
- Modify: test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs

**Interfaces:**
- Consumes: IStudioMemberWorkflowDraftProvisioningPort.
- Produces: aevatar_create_member_workflow_draft with team_id, display_name, workflow_yaml, optional member_id/workflow_id.

- [ ] **Step 1: Write failing tool/composition tests**

Assert owner scope precedence, unknown/scope/proof arguments rejected, typed success JSON, typed application errors, approval policy, source registration, and mainnet workspace composition.

- [ ] **Step 2: Verify RED**

~~~bash
dotnet test test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests/Aevatar.AI.ToolProviders.StudioProvisioning.Tests.csproj --nologo --filter "FullyQualifiedName~CreateStudioMemberWorkflowDraftToolTests|FullyQualifiedName~AddStudioProvisioningTools"
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter FullyQualifiedName~MainnetHostCompositionTests
~~~

- [ ] **Step 3: Implement the minimum tool/source**

Use snake-case JSON, additionalProperties=false, CreateScopedResource approval, mutating side-effect studio.workflow_draft.create, and StudioToolScopeResolver only. Map typed application failures without leaking unexpected exception details. Register one optional-port tool source and add it to the workspace tool set.

- [ ] **Step 4: Verify GREEN and commit**

Run Step 2, then commit the listed files with message Expose member workflow draft tool.

### Task 4: Migrate Studio prompt semantics and canonical docs

**Files:**
- Modify: src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionCatalog.cs
- Modify: test/Aevatar.Workflow.Host.Api.Tests/WorkflowDefinitionCatalogTests.cs
- Modify: docs/canon/nyxid-connected-service-tools.md
- Modify: docs/canon/workflow-runtime.md

- [ ] **Step 1: Write failing prompt tests**

Assert Studio exposes web_search, web_fetch, and the new draft tool; discovery precedes search/inference; unresolved operations create an unbound draft; search results cannot become selector/proof authority; unresolved drafts never call bind/schedule/provision.

- [ ] **Step 2: Verify RED**

~~~bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter FullyQualifiedName~BuiltInStudioYaml
~~~

- [ ] **Step 3: Update prompt and docs**

Keep exact descriptor bind/provision behavior. Replace the no-descriptor refusal with official-document search or minimal inference followed by draft creation. Document authoring evidence, draft command acceptance, exact bind admission, and runtime authorization as separate stages.

- [ ] **Step 4: Verify GREEN and commit**

~~~bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter FullyQualifiedName~BuiltInStudioYaml
bash tools/docs/lint.sh
git add src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionCatalog.cs test/Aevatar.Workflow.Host.Api.Tests docs/canon
git commit -m "Teach Studio to save unresolved workflow drafts"
~~~

### Task 5: Verify, integrate, and canary

- [ ] **Step 1: Run repository verification**

~~~bash
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
bash tools/ci/architecture_guards.sh
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/docs/lint.sh
git diff --check
~~~

- [ ] **Step 2: Rebase current integration and rerun build/test**

~~~bash
git fetch origin feature/integrate
git rebase origin/feature/integrate
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
~~~

- [ ] **Step 3: Push without force**

~~~bash
git push origin HEAD:feature/integrate
~~~

- [ ] **Step 4: After deployment, verify through NyxID only**

Run nyxid whoami, inspect nyxid proxy request --help, then POST workflow=studio to /api/chat via nyxid proxy request aevatar with SSE. Verify distinct member/draft IDs, canonical Studio URL, runnable=false, draft_save_accepted, and absence of bind/schedule/proxy dispatch. Capture redacted run/command evidence.

- [ ] **Step 5: Update issue #3025 from evidence**

Close only when the deployed canary passes and the original MCP catalog acceptance remains satisfied; otherwise leave it open with the exact run/command blocker.
