# Workflow Admission and Document Extraction Context Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make legacy direct NyxID connected-service tool names fail during workflow binding and preserve the workflow caller/LLM context in `document_extract` provider requests.

**Architecture:** Add one fail-closed check at the existing unified workflow invocation compiler. Carry the already-resolved caller credential plus a cloned typed LLM control object through `WorkflowToolExecutionRequest`, then map those values onto the two existing `LLMRequest` builders in `document_extract`.

**Tech Stack:** .NET 9, C# 13, protobuf generated workflow contracts, xUnit, FluentAssertions, NyxID CLI.

## Global Constraints

- Keep `nyxid_proxy` as the only admitted NyxID connected-service execution surface.
- Do not add another provider pipeline, provider factory, host-token fallback, credential store, or string-keyed metadata convention.
- Do not make Workflow Core depend on the NyxID tool-provider assembly.
- Do not make Workflow Infrastructure depend on `WorkflowCallerCredentialToolContextMapper`.
- Do not change non-provider-backed PDF, DOCX, or UTF-8 extraction.
- Bearer tokens must remain typed and must not enter metadata, logs, actor state, tool results, or test output.
- Every behavior change follows RED, minimal GREEN, focused regression, then commit.

---

### Task 1: Reject direct connected-service tool names in the shared compiler

**Files:**
- Modify: `test/Aevatar.Workflow.Core.Tests/WorkflowAuthorizationDependenciesTests.cs`
- Modify: `src/workflow/Aevatar.Workflow.Core/WorkflowAuthorizationDependencyEvaluator.cs:101-149`

**Interfaces:**
- Consumes: `WorkflowAuthorizationDependencyEvaluator.TryCompileExternalInvocation(InvocationStep)` and existing `MigrationInvalid(StepDefinition, string)`.
- Produces: binding-time `WorkflowExternalCapabilityValidationException` whose single blocker code is `NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED`.

- [ ] **Step 1: Add ordinary and synthesized failing tests**

Add these cases to `WorkflowAuthorizationDependenciesTests`:

```csharp
[Fact]
public void EvaluateAuthorizationDependencies_DirectNyxIdConnectedServiceTool_ShouldRequireMigration()
{
    const string yaml = """
        name: legacy-direct-tool
        roles: []
        steps:
          - id: list-records
            type: tool_call
            parameters:
              tool: nyxid_api-lark-bot-2__bitable_records_list
        """;

    var act = () => new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

    var exception = act.Should().Throw<WorkflowExternalCapabilityValidationException>().Which;
    exception.Readiness.Should().NotBeNull();
    exception.Readiness!.Blockers.Should().ContainSingle().Which.Code.Should()
        .Be("NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED");
}

[Fact]
public void EvaluateAuthorizationDependencies_SynthesizedDirectNyxIdTool_ShouldRequireMigration()
{
    const string yaml = """
        name: legacy-indirect-tool
        roles: []
        steps:
          - id: list-each
            type: foreach
            parameters:
              sub_step_type: tool_call
              sub_param_tool: nyxid_api-lark-bot-2__bitable_records_list
        """;

    var act = () => new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

    var exception = act.Should().Throw<WorkflowExternalCapabilityValidationException>().Which;
    exception.Readiness!.Blockers.Should().ContainSingle().Which.Code.Should()
        .Be("NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED");
}
```

- [ ] **Step 2: Run the focused test and confirm RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowAuthorizationDependenciesTests
```

Expected: both new tests fail because the compiler currently returns no external invocation and no exception for direct `nyxid_*__*` names. Existing `nyxid_proxy` tests remain green within the same run.

- [ ] **Step 3: Add the minimum compiler guard**

Add a private ordinal helper and invoke it immediately after template validation:

```csharp
if (IsDirectNyxIdConnectedServiceTool(toolName))
{
    throw MigrationInvalid(
        invocation.Step,
        "direct NyxID connected-service tool names are no longer supported; select an operation through nyxid_proxy and rebind.");
}

private static bool IsDirectNyxIdConnectedServiceTool(string toolName) =>
    toolName.StartsWith("nyxid_", StringComparison.OrdinalIgnoreCase) &&
    toolName.Contains("__", StringComparison.Ordinal);
```

Do not change `RequiresExternalCapabilityAdmission`; runtime admission remains scoped to canonical `nyxid_proxy`.

- [ ] **Step 4: Run focused tests and guards for GREEN**

Run:

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowAuthorizationDependenciesTests
bash tools/ci/workflow_binding_boundary_guard.sh
git diff --check
```

Expected: all commands exit 0.

- [ ] **Step 5: Commit the admission fix**

```bash
git add src/workflow/Aevatar.Workflow.Core/WorkflowAuthorizationDependencyEvaluator.cs \
  test/Aevatar.Workflow.Core.Tests/WorkflowAuthorizationDependenciesTests.cs
git commit -m "Reject legacy NyxID workflow tool names"
```

---

### Task 2: Carry the run's typed LLM controls through direct tool execution

**Files:**
- Modify: `test/Aevatar.Workflow.Core.Tests/Modules/ToolCallModuleContextTests.cs:276-325`
- Modify: `src/workflow/Aevatar.Workflow.Core/Modules/IWorkflowToolSource.cs:47-158`
- Modify: `src/workflow/Aevatar.Workflow.Core/Modules/ToolCallModule.cs:162-197`

**Interfaces:**
- Consumes: `WorkflowRunExecutionContextStateAccess.TryGetLlm`, `IWorkflowExecutionRuntimeContextAccessor.RuntimeContext.SenderNyxIdAccessToken`, and `WorkflowLlmControlContext.Clone()`.
- Produces: nullable `WorkflowToolExecutionRequest.LlmControl` containing model, route, tool-round, memory-prompt, and same-turn sender-token controls.

- [ ] **Step 1: Extend the existing direct-tool context test**

Before executing the tool, populate the existing typed state and runtime context:

```csharp
ctx.ExecutionContextState.Llm = new WorkflowLlmExecutionContextState
{
    ModelOverride = " model-alpha ",
    RoutePreference = " route-alpha ",
    UserMemoryPrompt = " remember-alpha ",
    MaxToolRoundsOverride = 4,
};
ctx.RuntimeContext.ApplySenderNyxIdAccessToken(" sender-alpha " );
```

Then add these assertions to `ToolCallModule_ShouldPassTypedWorkflowToolExecutionRequestToDirectTool`:

```csharp
tool.LastRequest.LlmControl.Should().NotBeNull();
tool.LastRequest.LlmControl!.ModelOverride.Should().Be("model-alpha");
tool.LastRequest.LlmControl.RoutePreference.Should().Be("route-alpha");
tool.LastRequest.LlmControl.UserMemoryPrompt.Should().Be("remember-alpha");
tool.LastRequest.LlmControl.MaxToolRoundsOverride.Should().Be(4);
tool.LastRequest.LlmControl.SenderNyxIdAccessToken.Should().Be("sender-alpha");
```

- [ ] **Step 2: Run the focused test and confirm RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo \
  --filter FullyQualifiedName~ToolCallModuleContextTests.ToolCallModule_ShouldPassTypedWorkflowToolExecutionRequestToDirectTool
```

Expected: compilation fails because `WorkflowToolExecutionRequest` has no `LlmControl` property.

- [ ] **Step 3: Add the cloned request field**

Append an optional constructor parameter and property in `WorkflowToolExecutionRequest`:

```csharp
WorkflowCapabilityInvocationAdmission? InvocationAdmission = null,
WorkflowLlmControlContext? LlmControl = null)
```

```csharp
this.InvocationAdmission = InvocationAdmission?.Clone();
this.LlmControl = LlmControl?.Clone();
```

```csharp
public WorkflowLlmControlContext? LlmControl { get; init; }
```

Existing short constructors continue to omit both optional fields.

- [ ] **Step 4: Build and pass the typed control in `ToolCallModule`**

Add a private helper that combines durable run LLM state with the existing same-turn sender token:

```csharp
private static WorkflowLlmControlContext? GetLlmControl(IWorkflowExecutionContext ctx)
{
    var hasLlm = WorkflowRunExecutionContextStateAccess.TryGetLlm(ctx, out var llm);
    var senderToken = ctx is IWorkflowExecutionRuntimeContextAccessor runtimeAccessor
        ? Normalize(runtimeAccessor.RuntimeContext.SenderNyxIdAccessToken)
        : null;
    if (!hasLlm && senderToken is null)
        return null;

    var control = new WorkflowLlmControlContext
    {
        ModelOverride = hasLlm ? Normalize(llm.ModelOverride) ?? string.Empty : string.Empty,
        RoutePreference = hasLlm ? Normalize(llm.RoutePreference) ?? string.Empty : string.Empty,
        UserMemoryPrompt = hasLlm ? Normalize(llm.UserMemoryPrompt) ?? string.Empty : string.Empty,
        SenderNyxIdAccessToken = senderToken ?? string.Empty,
    };
    if (hasLlm && llm.HasMaxToolRoundsOverride)
        control.MaxToolRoundsOverride = llm.MaxToolRoundsOverride;
    return control;
}
```

Pass `LlmControl: GetLlmControl(ctx)` in the existing `WorkflowToolExecutionRequest` call. Do not persist the sender token.

- [ ] **Step 5: Run context tests and test stability guard for GREEN**

Run:

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo \
  --filter FullyQualifiedName~ToolCallModuleContextTests
bash tools/ci/test_stability_guards.sh
git diff --check
```

Expected: all commands exit 0.

- [ ] **Step 6: Commit the direct-tool context change**

```bash
git add src/workflow/Aevatar.Workflow.Core/Modules/IWorkflowToolSource.cs \
  src/workflow/Aevatar.Workflow.Core/Modules/ToolCallModule.cs \
  test/Aevatar.Workflow.Core.Tests/Modules/ToolCallModuleContextTests.cs
git commit -m "Carry workflow LLM context to tools"
```

---

### Task 3: Apply caller and LLM context to document extraction provider requests

**Files:**
- Modify: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowDocumentExtractToolTests.cs:90-140`
- Modify: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowDocumentExtractToolTests.cs:899-944`
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowDocumentExtractToolSource.cs:56-290`
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowDocumentExtractToolSource.cs:542-663`

**Interfaces:**
- Consumes: `WorkflowToolExecutionRequest.CallerCredential`, `.ScopeId`, and `.LlmControl`.
- Produces: `LLMRequest.CallerContext` plus `LLMRequest.LlmControl` for plain image and schema-bound provider calls.

- [ ] **Step 1: Add provider-request assertions to both image paths**

In the schema-bound image test and plain image test, construct the tool request with:

```csharp
CallerCredential: new ProtoWorkflowCallerCredential { BearerToken = "caller-alpha" },
RuntimeContext: WorkflowToolRuntimeContext.Empty,
LlmControl: new Aevatar.Workflow.Abstractions.WorkflowLlmControlContext
{
    ModelOverride = "model-alpha",
    RoutePreference = "route-alpha",
    MaxToolRoundsOverride = 5,
    UserMemoryPrompt = "memory-alpha",
    SenderNyxIdAccessToken = "sender-alpha",
})
```

For each captured `LLMRequest`, assert:

```csharp
request.CallerContext.Should().NotBeNull();
request.CallerContext!.ScopeId.Should().Be("scope-1");
request.CallerContext.OwnerSubject.Should().Be("scope-1");
request.CallerContext.Credentials!.NyxIdBearer.Should().Be("caller-alpha");
request.LlmControl.Should().NotBeNull();
request.LlmControl!.ModelOverride.Should().Be("model-alpha");
request.LlmControl.NyxIdRoutePreference.Should().Be("route-alpha");
request.LlmControl.MaxToolRoundsOverride.Should().Be(5);
request.LlmControl.UserMemoryPrompt.Should().Be("memory-alpha");
request.LlmControl.SenderNyxIdAccessToken.Should().Be("sender-alpha");
```

- [ ] **Step 2: Run the focused test class and confirm RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowDocumentExtractToolTests
```

Expected: the two new assertions fail because both provider requests have null caller and LLM contexts.

- [ ] **Step 3: Thread the execution request through provider-backed methods**

Add `WorkflowToolExecutionRequest request` to the private image/schema provider methods and pass the original request from `ExecuteAsync`. This includes text-backed schema extraction because it uses the same provider builder. Do not alter PDF/DOCX/UTF-8 extraction itself.

- [ ] **Step 4: Add narrow typed mapping helpers**

Use the file's existing `Normalize` helper:

```csharp
private static LLMRequestCallerContext ToCallerContext(WorkflowToolExecutionRequest request)
{
    var scopeId = Normalize(request.ScopeId) ?? string.Empty;
    var bearer = Normalize(request.CallerCredential?.BearerToken);
    return new LLMRequestCallerContext(
        scopeId,
        scopeId,
        ResponseId: null,
        bearer is null ? null : new LLMRequestCallerCredentials(bearer));
}

private static LLMControlContext? ToLlmControl(WorkflowLlmControlContext? source) =>
    source is null
        ? null
        : new LLMControlContext(
            NyxIdAccessToken: null,
            NyxIdOrgToken: null,
            SenderNyxIdAccessToken: Normalize(source.SenderNyxIdAccessToken),
            ModelOverride: Normalize(source.ModelOverride),
            NyxIdRoutePreference: Normalize(source.RoutePreference),
            MaxToolRoundsOverride: source.HasMaxToolRoundsOverride
                ? source.MaxToolRoundsOverride
                : null,
            UserMemoryPrompt: Normalize(source.UserMemoryPrompt));
```

Set both fields in both `new LLMRequest` initializers:

```csharp
CallerContext = ToCallerContext(request),
LlmControl = ToLlmControl(request.LlmControl),
```

Do not copy bearer data into `LlmControl`, `Metadata`, messages, request IDs, or exception text.

- [ ] **Step 5: Run document extraction regression tests for GREEN**

Run:

```bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowDocumentExtractToolTests
bash tools/ci/test_stability_guards.sh
git diff --check
```

Expected: all commands exit 0, including existing error redaction and file-size tests.

- [ ] **Step 6: Commit the document extraction mapping**

```bash
git add src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowDocumentExtractToolSource.cs \
  test/Aevatar.Workflow.Host.Api.Tests/WorkflowDocumentExtractToolTests.cs
git commit -m "Preserve workflow context in document extraction"
```

---

### Task 4: Verify, synchronize, push, and run production canaries

**Files:**
- Verify: all files committed by Tasks 1-3
- Read-only fixtures: `~/workflows/2026-07-30-for-aevatar-team/P2-budget-monitor/budget_monitor_weekly.nosend.yaml`
- Read-only fixtures: `~/workflows/2026-07-30-for-aevatar-team/P1-invoice-approval/attach-probe.provision-body.json`

**Interfaces:**
- Consumes: committed branch plus authenticated `nyxid proxy request aevatar ...`.
- Produces: pushed `origin/feature/integrate` commit and redacted binding/run evidence for issues #3102 and #3103.

- [ ] **Step 1: Run repository verification**

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowAuthorizationDependenciesTests
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo \
  --filter FullyQualifiedName~ToolCallModuleContextTests
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowDocumentExtractToolTests
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
git diff --check
git status --short
```

Expected: every verification exits 0 and worktree status is clean.

- [ ] **Step 2: Rebase onto the latest integration branch and rerun risk-focused checks**

```bash
git fetch origin feature/integrate
git rebase origin/feature/integrate
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowAuthorizationDependenciesTests
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo \
  --filter FullyQualifiedName~ToolCallModuleContextTests
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo \
  --filter FullyQualifiedName~WorkflowDocumentExtractToolTests
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/architecture_guards.sh
dotnet build aevatar.slnx --nologo
git diff --check
```

Expected: rebase is conflict-free or conflicts are resolved deliberately, then all focused checks exit 0.

- [ ] **Step 3: Push the reviewed commits directly to integration**

```bash
git push origin HEAD:feature/integrate
```

Expected: non-force push succeeds and the reported remote SHA matches local `HEAD`.

- [ ] **Step 4: Confirm the NyxID CLI identity and transport contract**

```bash
nyxid whoami
nyxid proxy request --help
```

Expected: identity succeeds without displaying credentials. If it fails, use `nyxid doctor`; do not switch to curl, browser tokens, cookies, Kubernetes exec, or service credentials.

- [ ] **Step 5: Verify #3102 after the integration deployment**

Read the fixed `teamId` and caller identity from `attach-probe.provision-body.json`, use the same production scope used by the prior canary, and submit `budget_monitor_weekly.nosend.yaml` through:

```bash
nyxid proxy request aevatar /api/scopes/5d0d7b72-acff-49af-bb1b-9f30bbb7c102/provision-workflow \
  --method POST \
  --header 'Content-Type:application/json' \
  --data @/tmp/aevatar-3102-provision.json \
  --output json
```

`/tmp/aevatar-3102-provision.json` must contain the unchanged P2 YAML plus the already-confirmed team/caller values; it must contain no credentials. The scope is the authority returned by the prior canary binding and matches the signed-in NyxID subject. Expected: binding fails closed with `NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED`, and no succeeded serving revision is created. Record the binding command/correlation ID and typed blocker.

- [ ] **Step 6: Verify #3103 with the existing real-PNG attachment probe**

Reuse the already-bound attachment-probe member and send the existing 1280×720 PNG through the NyxID proxy:

```bash
boundary='codex-aevatar-verify-20260801'
{
  printf -- '--%s\r\nContent-Disposition: form-data; name="payload"\r\nContent-Type: application/json\r\n\r\n%s\r\n' \
    "$boundary" \
    '{"prompt":"verify issue 3103 image extraction after integration deployment","headers":{"channel":"codex-prod-verify"}}'
  printf -- '--%s\r\nContent-Disposition: form-data; name="file"; filename="workflow-studio-canvas-desktop-1280x720.png"\r\nContent-Type: image/png\r\n\r\n' \
    "$boundary"
  command cat /Users/eanzhao/Code/aevatar/apps/aevatar-console-web/docs/visual-evidence/2026-07-22-workflow-studio-canvas/workflow-studio-canvas-desktop-1280x720.png
  printf '\r\n--%s--\r\n' "$boundary"
} | nyxid proxy request aevatar \
  /api/scopes/5d0d7b72-acff-49af-bb1b-9f30bbb7c102/members/wf-8257591904f0f5f01da20a9e39445c97/invoke/chat:stream \
  --method POST \
  --header "Content-Type:multipart/form-data; boundary=$boundary" \
  --header 'Accept:text/event-stream' \
  --data - \
  --stream
```

Read the returned run URL through the same CLI. Expected: `extract_item_0` completes, terminal state is successful, and the extracted text is non-empty. Record only the run ID, command/correlation ID, file-ref ID, terminal state, and sanitized output evidence.

- [ ] **Step 7: Report issue evidence**

Comment on #3102 and #3103 with the pushed commit SHA, exact redacted command shapes, binding/run IDs, typed terminal results, and any deployment lag. Close an issue only when its production canary passes; do not treat local tests or user-facing LLM prose as production success evidence.
