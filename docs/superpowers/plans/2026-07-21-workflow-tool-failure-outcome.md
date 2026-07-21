# Workflow Tool Failure Outcome Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make provider-declared and workflow-native tool failures produce failed workflow steps and failed runs instead of silent success.

**Architecture:** Extend the existing in-process `WorkflowToolExecutionResult` with a typed failure outcome, map provider-owned `AgentToolReceipt` failures at the AI integration adapter, and let `ToolCallModule` publish the existing failed step/event contracts. Workflow Core never parses result JSON; the existing execution kernel remains the single retry/on_error/terminal-failure path.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, Protobuf workflow events.

## Global Constraints

- Keep `IWorkflowTool.ExecuteAsync(WorkflowToolExecutionRequest, CancellationToken)` unchanged.
- Do not infer failure from arbitrary JSON in Workflow Core or frontend code.
- Preserve safe failure result JSON as event/step output and surface only typed safe error text.
- Preserve `ScheduledDispatchGAgent.Dispatched` as transport acceptance.
- Add no process-local workflow/run/session fact maps.
- Run `bash tools/ci/test_stability_guards.sh` for all test changes.
- Update the canonical workflow primitive documentation.

---

### Task 1: Typed Workflow Tool Failure And Module Propagation

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Core/Modules/IWorkflowToolSource.cs`
- Modify: `src/workflow/Aevatar.Workflow.Core/Modules/ToolCallModule.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/Modules/ToolCallModuleContextTests.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/Modules/ToolCallModuleApprovalTests.cs`
- Test: `test/Aevatar.Integration.Tests/WorkflowTuringCompletenessTests.cs`

**Interfaces:**
- Consumes: existing `WorkflowToolExecutionResult`, `WorkflowToolCallCompletedEvent`, and `StepCompletedEvent`.
- Produces: `WorkflowToolExecutionFailure`, `WorkflowToolExecutionResult.Failed(...)`, and one module publication path shared by initial execution and approved replay.

- [ ] **Step 1: Write failing direct and approved-replay tests**

Add a direct tool returning:

```csharp
WorkflowToolExecutionResult.Failed(
    """{"error":true,"status":503}""",
    "NYXID_PROXY_HTTP_503",
    "The service request failed.")
```

Assert both `WorkflowToolCallCompletedEvent` and `StepCompletedEvent` have
`Success=false`, preserve the JSON in `ResultJson`/`Output`, and expose a safe
error containing the tool name, code, and message. Add the same assertions after
an approval-pending result is resumed and the replay returns `Failed(...)`.

Add a one-step `tool_call` workflow to `WorkflowTuringCompletenessTests` and pass
a `ToolCallModule` containing the same failing tool into the closed-world helper.
Assert the terminal `WorkflowCompletedEvent` has `Success=false` and its error
contains `NYXID_PROXY_HTTP_503` and `The service request failed.`

- [ ] **Step 2: Run tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~ToolCallModuleContextTests|FullyQualifiedName~ToolCallModuleApprovalTests"
dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~WorkflowTuringCompletenessTests"
```

Expected: compilation fails because `WorkflowToolExecutionResult.Failed` and the
typed failure outcome do not exist.

- [ ] **Step 3: Add the typed outcome contract**

Extend the result without changing the interface method:

```csharp
public sealed record WorkflowToolExecutionFailure(
    string ErrorCode,
    string ErrorMessage);

public sealed record WorkflowToolExecutionResult(
    string ResultJson,
    WorkflowManagedHandoffOutcome? ManagedHandoff = null,
    WorkflowToolApprovalPendingOutcome? PendingApproval = null,
    WorkflowToolExecutionFailure? Failure = null)
{
    public static WorkflowToolExecutionResult Success(
        string resultJson,
        WorkflowManagedHandoffOutcome? managedHandoff = null) =>
        new(resultJson ?? string.Empty, managedHandoff);

    public static WorkflowToolExecutionResult Failed(
        string resultJson,
        string errorCode,
        string errorMessage) =>
        new(
            resultJson ?? string.Empty,
            Failure: new WorkflowToolExecutionFailure(
                errorCode ?? string.Empty,
                errorMessage ?? string.Empty));
}
```

- [ ] **Step 4: Publish typed failures through existing workflow events**

Replace direct and resume success-only publication with a shared helper. When
`result.Failure` is present, call the failure publisher with the failure code,
message, and result JSON. Populate `WorkflowToolCallCompletedEvent.ResultJson`
and `StepCompletedEvent.Output` on failure. Keep exception and missing-tool paths
working with empty code/result values.

Format the run-visible error deterministically:

```csharp
var detail = string.IsNullOrWhiteSpace(errorCode)
    ? error
    : $"{errorCode}: {error}";
var errorMessage = $"tool '{toolName}' execution failed: {detail}";
```

- [ ] **Step 5: Run tests and verify GREEN**

Run both Task 1 test commands. Expected: all selected tests pass with zero
failures, including the terminal run assertion.

- [ ] **Step 6: Commit Task 1**

```bash
git add src/workflow/Aevatar.Workflow.Core/Modules/IWorkflowToolSource.cs src/workflow/Aevatar.Workflow.Core/Modules/ToolCallModule.cs test/Aevatar.Workflow.Core.Tests/Modules/ToolCallModuleContextTests.cs test/Aevatar.Workflow.Core.Tests/Modules/ToolCallModuleApprovalTests.cs test/Aevatar.Integration.Tests/WorkflowTuringCompletenessTests.cs
git commit -m "Propagate typed workflow tool failures"
```

### Task 2: Provider Receipt And Workflow-Native Tool Mapping

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Integration.AI/AgentWorkflowToolSourceAdapter.cs`
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowDocumentExtractToolSource.cs`
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowSpreadsheetExtractToolSource.cs`
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowConnectedServiceResourceFetchToolSource.cs`
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowFileSubmitToolSource.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/Modules/AgentWorkflowToolSourceAdapterTests.cs`
- Test: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowDocumentExtractToolTests.cs`
- Test: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowSpreadsheetExtractToolTests.cs`
- Test: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowConnectedServiceResourceFetchToolTests.cs`
- Test: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowFileSubmitToolTests.cs`

**Interfaces:**
- Consumes: `AgentToolReceiptStatus` and Task 1's `WorkflowToolExecutionResult.Failed(...)`.
- Produces: provider-classified receipt failures and workflow-native error helpers mapped to the same typed failure result.

- [ ] **Step 1: Write failing adapter classification tests**

Add an `IAgentTool` test double whose `CreateResultReceipt` returns an error
receipt with safe `ResultJson`, `ErrorCode`, and `ErrorMessage`. Assert the
adapted workflow result has a matching `Failure`. Add a second tool whose normal
result contains an arbitrary `error` field but returns no receipt; assert its
`Failure` is null to prove Workflow Core does not guess from JSON.

- [ ] **Step 2: Write failing workflow-native error tests**

In the existing missing/invalid input test for each workflow-native source,
assert the concrete code already serialized by that source:

```csharp
documentResult.Failure!.ErrorCode.Should().Be("unsupported_media_type");
spreadsheetResult.Failure!.ErrorCode.Should().Be("unsupported_media_type");
resourceFetchResult.Failure!.ErrorCode.Should().Be("invalid_arguments");
fileSubmitResult.Failure!.ErrorCode.Should().Be("invalid_arguments");
```

For each result also assert `Failure` is non-null and `ErrorMessage` is non-empty.

- [ ] **Step 3: Run tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~AgentWorkflowToolSourceAdapterTests"
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~WorkflowDocumentExtractToolTests|FullyQualifiedName~WorkflowSpreadsheetExtractToolTests|FullyQualifiedName~WorkflowConnectedServiceResourceFetchToolTests|FullyQualifiedName~WorkflowFileSubmitToolTests"
```

Expected: adapter and native-tool failure assertions fail because the current
code wraps every normally returned result as success.

- [ ] **Step 4: Map non-success provider receipts**

After `ToolCallReceiptFinalizer.Finalize`, map `Error`, `Denied`, and
`AuthorizationRequired` to `WorkflowToolExecutionResult.Failed`. Prefer the
receipt's safe result JSON over the raw tool result, and fall back to stable
status-derived codes/messages only when the receipt omits them. Map `Success`
and normalized `Unspecified` to `Success(resultJson, managedHandoff)`.

- [ ] **Step 5: Convert workflow-native error helpers**

Serialize each existing structured error once, then return:

```csharp
WorkflowToolExecutionResult.Failed(resultJson, error, detail)
```

Do not change their successful payloads or external JSON shapes.

- [ ] **Step 6: Run tests and verify GREEN**

Run both Task 2 commands. Expected: all selected tests pass.

- [ ] **Step 7: Commit Task 2**

```bash
git add src/workflow/Aevatar.Workflow.Integration.AI/AgentWorkflowToolSourceAdapter.cs src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowDocumentExtractToolSource.cs src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowSpreadsheetExtractToolSource.cs src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowConnectedServiceResourceFetchToolSource.cs src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowFileSubmitToolSource.cs test/Aevatar.Workflow.Core.Tests/Modules/AgentWorkflowToolSourceAdapterTests.cs test/Aevatar.Workflow.Host.Api.Tests/WorkflowDocumentExtractToolTests.cs test/Aevatar.Workflow.Host.Api.Tests/WorkflowSpreadsheetExtractToolTests.cs test/Aevatar.Workflow.Host.Api.Tests/WorkflowConnectedServiceResourceFetchToolTests.cs test/Aevatar.Workflow.Host.Api.Tests/WorkflowFileSubmitToolTests.cs
git commit -m "Map tool provider failures into workflows"
```

### Task 3: Canon And Verification

**Files:**
- Modify: `docs/canon/workflow-primitives.md`

**Interfaces:**
- Consumes: Task 1's failed step/run contract and Task 2's provider mappings.
- Produces: canonical documentation plus repository-wide verification evidence.

- [ ] **Step 1: Document the canonical outcome contract**

Update the `tool_call` section of `docs/canon/workflow-primitives.md` to state:

- adapters/providers own external result classification;
- typed failures enter retry, `on_error`, compensation, and terminal failure;
- Workflow Core and frontend code do not infer status from arbitrary output JSON;
- existing workflows that returned success-wrapped errors now correctly fail or
  follow their configured recovery policy.

- [ ] **Step 2: Run focused and guard verification**

Run:

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~ToolCallModule|FullyQualifiedName~AgentWorkflowToolSourceAdapter"
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~WorkflowDocumentExtractToolTests|FullyQualifiedName~WorkflowSpreadsheetExtractToolTests|FullyQualifiedName~WorkflowConnectedServiceResourceFetchToolTests|FullyQualifiedName~WorkflowFileSubmitToolTests"
dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~WorkflowTuringCompletenessTests"
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

Expected: all commands exit 0.

- [ ] **Step 3: Run complete build and test verification**

```bash
dotnet build aevatar.slnx --nologo --no-restore
dotnet test aevatar.slnx --nologo --no-build --no-restore
```

Expected: build and all owned tests pass with zero failures.

- [ ] **Step 4: Commit Task 3**

```bash
git add docs/canon/workflow-primitives.md docs/superpowers/plans/2026-07-21-workflow-tool-failure-outcome.md
git commit -m "Verify workflow tool failure outcomes"
```

- [ ] **Step 5: Rebase and push directly to integration**

Fetch `origin/feature/integrate`, rebase the repair commits if the remote moved,
rerun affected verification after any rebase, and push with:

```bash
git push origin HEAD:feature/integrate
```

Expected: the remote branch advances to the verified repair head without a
force push.
