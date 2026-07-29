# Admin Observatory Human Approval Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the owner of an ad-hoc `/api/chat` run approve or reject a committed `human_approval` suspension from `/admin`, while keeping cross-scope admin views read-only.

**Architecture:** Preserve `WorkflowSuspendedEvent` type, prompt, content, and timeout through the existing protobuf-backed run report and Observatory DTO. The static `/admin` page derives an action-required card only from those typed committed facts and submits decisions through the existing scope-first `POST /api/scopes/:scopeId/runs/:runId:resume` command path. Observatory remains a read-only query API; no actor state read, query-time priming, or second command surface is added.

**Tech Stack:** .NET 10, ASP.NET Core, Protobuf, xUnit, FluentAssertions, embedded HTML/CSS/JavaScript, Node.js `vm` behavior tests.

## Global Constraints

- Only `detail.summary.scopeId === authenticated /api/workflow/observatory/me scopeId` grants approval controls. Admin elevation and cross-scope filters never grant command authority.
- Approval eligibility comes only from an incomplete step with typed `suspensionType === "human_approval"`; never parse step ids, diagnostics, timeline messages, metadata, or actor id strings.
- Reuse `POST /api/scopes/:scopeId/runs/:runId:resume`; do not add an Observatory mutation endpoint or restore `/api/workflows/resume`.
- Omit `actorId` so the scope-first Host resolves the opaque run actor from the authoritative binding.
- Approve preserves the committed pending content. Reject requires non-blank feedback and sends it as `userInput`, using the existing `HumanApprovalModule.ResolveFeedback` fallback.
- Treat `202 Accepted` as dispatch acceptance only; rely on the existing three-second read-model refresh for committed progress.
- Secure suspensions never materialize `suspension_content`; other suspension content must pass `WorkflowAuditTextSanitizer`.
- Do not add dependencies, interfaces, factories, or compatibility aliases.
- Preserve unrelated working-tree changes, especially `test/Aevatar.Capabilities.Tests/BackendConsoleAssetServiceTests.cs`.

---

### Task 1: Preserve Typed Suspension Content Through the Committed Run Report

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Projection/workflow_projection_transport.proto`
- Modify: `src/workflow/Aevatar.Workflow.Application.Abstractions/Queries/WorkflowExecutionQueryModels.cs`
- Modify: `src/workflow/Aevatar.Workflow.Application.Abstractions/Security/WorkflowAuditReportSanitizer.cs`
- Modify: `src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionArtifactMaterializationSupport.cs`
- Modify: `src/workflow/Aevatar.Workflow.Projection/ReadModels/WorkflowExecutionReadModelMapper.cs`
- Test: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowExecutionProjectionProjectorTests.cs`

**Interfaces:**
- Consumes: committed `WorkflowSuspendedEvent.Content`, `.Secure`, `.SuspensionType`, `.Prompt`, and `.TimeoutSeconds`.
- Produces: `WorkflowExecutionStepTrace.SuspensionContent` protobuf field 21 and `WorkflowRunStepTrace.SuspensionContent : string`.

- [ ] **Step 1: Add a failing end-to-end projection test**

Add this focused test to `WorkflowExecutionProjectionProjectorTests`:

```csharp
[Fact]
public void ApplyObservedPayloadToReport_ShouldPreserveSanitizedApprovalContent_AndHideSecureContent()
{
    const string secret = "suspension-secret";
    var document = new WorkflowRunInsightReportDocument();

    WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
        document,
        PackStateEvent(
            new WorkflowSuspendedEvent
            {
                StepId = "review",
                SuspensionType = "human_approval",
                Prompt = "Review the draft",
                Content = $$"""{"draft":"ready","access_token":"{{secret}}"}""",
                TimeoutSeconds = 3600,
            },
            1,
            "evt-review"),
        DateTimeOffset.UnixEpoch);
    WorkflowExecutionArtifactMaterializationSupport.ApplyObservedPayloadToReport(
        document,
        PackStateEvent(
            new WorkflowSuspendedEvent
            {
                StepId = "secret",
                SuspensionType = "secure_input",
                Content = "must-not-materialize",
                Secure = true,
            },
            2,
            "evt-secret"),
        DateTimeOffset.UnixEpoch.AddSeconds(1));

    var report = new WorkflowExecutionReadModelMapper().ToRunReport(document);
    var sanitized = WorkflowAuditReportSanitizer.Sanitize(report);

    var review = sanitized.Steps.Single(step => step.StepId == "review");
    review.SuspensionContent.Should().Contain("\"draft\":\"ready\"");
    review.SuspensionContent.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
    review.SuspensionContent.Should().NotContain(secret);
    review.SuspensionTimeoutSeconds.Should().Be(3600);
    sanitized.Steps.Single(step => step.StepId == "secret")
        .SuspensionContent.Should().BeEmpty();
}
```

Add the existing namespaces `Aevatar.Workflow.Application.Abstractions.Security` and `Aevatar.Workflow.Abstractions.Security` if the test file does not already import them.

- [ ] **Step 2: Run the test and verify RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj \
  --nologo \
  --filter 'FullyQualifiedName~ApplyObservedPayloadToReport_ShouldPreserveSanitizedApprovalContent_AndHideSecureContent'
```

Expected: compilation fails because `WorkflowRunStepTrace.SuspensionContent` does not exist. This is the intended RED signal.

- [ ] **Step 3: Add the minimal typed field and mappings**

Append field 21 to `WorkflowExecutionStepTrace` without renumbering existing fields:

```proto
string suspension_content = 21;
```

Add this property beside `SuspensionPrompt` on `WorkflowRunStepTrace`:

```csharp
public string SuspensionContent { get; set; } = string.Empty;
```

In `WorkflowExecutionArtifactMaterializationSupport.ApplyWorkflowSuspended`, materialize only non-secure content:

```csharp
step.SuspensionContent = evt.Secure
    ? string.Empty
    : SanitizeAuditText(evt.Content);
```

Copy `SuspensionContent` in `CloneStepTrace`, `WorkflowExecutionReadModelMapper.MapStepTrace`, and `WorkflowAuditReportSanitizer.SanitizeStep`:

```csharp
SuspensionContent = source.SuspensionContent,
```

Use `WorkflowAuditTextSanitizer.Sanitize(step.SuspensionContent)` in the sanitizer rather than copying raw text.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the command from Step 2. Expected: one passing test, zero failures.

- [ ] **Step 5: Run projection regression tests**

Run:

```bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj \
  --nologo \
  --filter 'FullyQualifiedName~WorkflowExecutionProjectionProjectorTests|FullyQualifiedName~WorkflowProjectionMaterializationTests|FullyQualifiedName~WorkflowProjectionReadModelCoverageTests'
```

Expected: all selected projection tests pass.

- [ ] **Step 6: Commit the projection slice**

```bash
git add \
  src/workflow/Aevatar.Workflow.Projection/workflow_projection_transport.proto \
  src/workflow/Aevatar.Workflow.Application.Abstractions/Queries/WorkflowExecutionQueryModels.cs \
  src/workflow/Aevatar.Workflow.Application.Abstractions/Security/WorkflowAuditReportSanitizer.cs \
  src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionArtifactMaterializationSupport.cs \
  src/workflow/Aevatar.Workflow.Projection/ReadModels/WorkflowExecutionReadModelMapper.cs \
  test/Aevatar.Workflow.Host.Api.Tests/WorkflowExecutionProjectionProjectorTests.cs
git commit -m "Preserve workflow suspension content"
```

---

### Task 2: Expose Typed Suspension Facts From Observatory Detail

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Application.Abstractions/Observatory/IWorkflowRunObservatoryQueryService.cs`
- Modify: `src/workflow/Aevatar.Workflow.Application/Observatory/WorkflowRunObservatoryQueryService.cs`
- Test: `test/Aevatar.Workflow.Application.Tests/WorkflowRunObservatoryQueryServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 `WorkflowRunStepTrace.SuspensionContent`.
- Produces: `ObservatoryStepDetail.SuspensionType`, `.SuspensionPrompt`, `.SuspensionContent`, and `.SuspensionTimeoutSeconds`.

- [ ] **Step 1: Add a failing Observatory behavior test**

Add this test to `WorkflowRunObservatoryQueryServiceTests`:

```csharp
[Fact]
public async Task GetRunForScopeAsync_ShouldExposeActiveHumanApprovalFacts()
{
    var snapshot = Snapshot(
        "run-1",
        CallerScope,
        WorkflowRunCompletionStatus.Running,
        started: 1,
        updated: 5);
    var currentState = new FakeCurrentStateQueryPort { SingleResult = snapshot };
    var report = new WorkflowRunReport
    {
        Steps =
        [
            new WorkflowRunStepTrace
            {
                StepId = "show_for_approval",
                StepType = "human_approval",
                RequestedAt = DateTimeOffset.UnixEpoch.AddSeconds(4),
                SuspensionType = "human_approval",
                SuspensionPrompt = "Review the generated workflow YAML.",
                SuspensionContent = "name: daily_tech_digest\nsteps: []",
                SuspensionTimeoutSeconds = 3600,
            },
        ],
    };
    var service = new WorkflowRunObservatoryQueryService(
        currentState,
        new FakeArtifactQueryPort { Report = report });

    var detail = await service.GetRunForScopeAsync(CallerScope, "run-1");

    var approval = detail!.Steps.Should().ContainSingle().Subject;
    approval.CompletedAtUtc.Should().BeNull();
    approval.SuspensionType.Should().Be("human_approval");
    approval.SuspensionPrompt.Should().Be("Review the generated workflow YAML.");
    approval.SuspensionContent.Should().Be("name: daily_tech_digest\nsteps: []");
    approval.SuspensionTimeoutSeconds.Should().Be(3600);
}
```

- [ ] **Step 2: Run the test and verify RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj \
  --nologo \
  --filter 'FullyQualifiedName~GetRunForScopeAsync_ShouldExposeActiveHumanApprovalFacts'
```

Expected: compilation fails because the four `ObservatoryStepDetail` properties do not exist.

- [ ] **Step 3: Add the minimal Observatory DTO fields and direct mapping**

Add these properties to `ObservatoryStepDetail` after `BranchKey`:

```csharp
public string SuspensionType { get; init; } = string.Empty;
public string SuspensionPrompt { get; init; } = string.Empty;
public string SuspensionContent { get; init; } = string.Empty;
public int? SuspensionTimeoutSeconds { get; init; }
```

Copy them in `WorkflowRunObservatoryQueryService.ToStepDetail`:

```csharp
SuspensionType = step.SuspensionType,
SuspensionPrompt = step.SuspensionPrompt,
SuspensionContent = step.SuspensionContent,
SuspensionTimeoutSeconds = step.SuspensionTimeoutSeconds,
```

Do not add a separate pending-approval DTO: the active step plus typed suspension fields are sufficient.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the command from Step 2. Expected: one passing test, zero failures.

- [ ] **Step 5: Run the complete Observatory Application tests**

Run:

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj \
  --nologo \
  --filter 'FullyQualifiedName~WorkflowRunObservatory'
```

Expected: all selected Observatory tests pass.

- [ ] **Step 6: Commit the Observatory contract slice**

```bash
git add \
  src/workflow/Aevatar.Workflow.Application.Abstractions/Observatory/IWorkflowRunObservatoryQueryService.cs \
  src/workflow/Aevatar.Workflow.Application/Observatory/WorkflowRunObservatoryQueryService.cs \
  test/Aevatar.Workflow.Application.Tests/WorkflowRunObservatoryQueryServiceTests.cs
git commit -m "Expose workflow approval facts"
```

---

### Task 3: Render and Submit Owner-Only Approval Actions in `/admin`

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`
- Test: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs`

**Interfaces:**
- Consumes: Task 2 JSON step fields `suspensionType`, `suspensionPrompt`, `suspensionContent`, `suspensionTimeoutSeconds`; authenticated `ACCOUNT.scope`; existing `adminJson`.
- Produces: `obsActiveApproval(run)`, `obsCanApprove(run)`, `obsApprovalPanel(run)`, and `obsSubmitApproval(run, approved, rerender)` behaviors.

- [ ] **Step 1: Add a failing Node `vm` behavior test against the served asset**

Add `AdminShell_ObservatoryHumanApproval_ShouldBeTypedOwnerOnlyAndUseScopeResume` to `BackendConsoleStaticAssetEndpointTests`. Follow the file's existing Node `vm` harness: read the served `/admin` HTML from stdin, extract production functions by name, and execute them with controlled state. The JavaScript assertions must cover this literal fixture and outcomes:

```javascript
const raw = {
  summary: {
    runId: 'run-approval',
    workflowName: 'auto_review',
    status: 'running',
    scopeId: 'scope-owner',
    stateVersion: 58
  },
  steps: [{
    stepId: 'show_for_approval',
    stepType: 'human_approval',
    requestedAtUtc: '2026-07-29T02:38:47Z',
    suspensionType: 'human_approval',
    suspensionPrompt: 'Review this workflow',
    suspensionContent: 'name: daily_tech_digest\nsteps: []',
    suspensionTimeoutSeconds: 3600
  }],
  diagnostics: [{ severity: 'info', code: 'active_step', message: 'waiting' }]
};

const run = mapObsDetail(raw, null);
assert.equal(run.steps[0].suspensionType, 'human_approval');
assert.equal(run.steps[0].suspensionContent, 'name: daily_tech_digest\nsteps: []');

ACCOUNT = { scope: 'scope-owner', admin: false };
let panel = obsApprovalPanel(run);
assert.match(panel, /需要审批/);
assert.match(panel, /daily_tech_digest/);
assert.match(panel, /data-act="obsApprovalApprove"/);
assert.doesNotMatch(obsDiagnosticStrip(run), /失败诊断/);
assert.match(obsDiagnosticStrip(run), /当前位置/);

await obsSubmitApproval(run, true, function() {});
assert.equal(requests[0].path, '/api/scopes/scope-owner/runs/run-approval:resume');
assert.deepEqual(JSON.parse(requests[0].options.body), {
  stepId: 'show_for_approval',
  approved: true
});

ACCOUNT = { scope: 'scope-admin', admin: true };
panel = obsApprovalPanel(run);
assert.match(panel, /只读/);
assert.doesNotMatch(panel, /obsApprovalApprove/);

ACCOUNT = { scope: 'scope-owner', admin: false };
const state = obsApprovalState(run.id);
state.rejecting = true;
state.feedback = '  ';
assert.equal(await obsSubmitApproval(run, false, function() {}), false);
assert.equal(requests.length, 1);
state.feedback = '请补充来源';
assert.equal(await obsSubmitApproval(run, false, function() {}), true);
assert.deepEqual(JSON.parse(requests[1].options.body), {
  stepId: 'show_for_approval',
  approved: false,
  userInput: '请补充来源'
});

assert.match(obsDiagnosticStrip({
  status: 'failed',
  rawStatus: 'failed',
  diagnostics: [{ severity: 'error', code: 'step_failed', message: 'boom' }]
}), /失败诊断/);
```

The harness must execute the real `mapObsDetail`, `obsActiveApproval`, `obsApprovalState`, `obsCanApprove`, `obsApprovalPanel`, `obsSubmitApproval`, and `obsDiagnosticStrip` functions. Stub only their external boundaries (`adminJson`, rendering callback, time/format helpers), mirroring the complete fixture fields above.

- [ ] **Step 2: Run the behavior test and verify RED**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj \
  --nologo \
  --filter 'FullyQualifiedName~AdminShell_ObservatoryHumanApproval_ShouldBeTypedOwnerOnlyAndUseScopeResume'
```

Expected: FAIL because suspension fields are not mapped and the approval functions/actions do not exist.

- [ ] **Step 3: Map typed suspension fields in `mapObsDetail`**

Extend each mapped step with direct JSON fields:

```javascript
suspensionType:s.suspensionType||'',
suspensionPrompt:s.suspensionPrompt||'',
suspensionContent:s.suspensionContent||'',
suspensionTimeoutSeconds:s.suspensionTimeoutSeconds
```

Do not inspect `stepId`, `diagnostics`, or timeline messages when deciding eligibility.

- [ ] **Step 4: Add minimal per-run action state and pure approval selectors**

Define one small state map beside the existing Observatory globals:

```javascript
var OBS_APPROVAL={};
function obsApprovalState(runId){
  return OBS_APPROVAL[runId]||(OBS_APPROVAL[runId]={rejecting:false,feedback:'',submitting:false,accepted:false,error:''});
}
function obsActiveApproval(run){
  return (run.steps||[]).filter(function(step){
    return !step.completedAtUtc&&String(step.suspensionType||'').toLowerCase()==='human_approval';
  }).slice(-1)[0]||null;
}
function obsCanApprove(run){
  return !!(ACCOUNT&&ACCOUNT.scope&&run&&run.scope===ACCOUNT.scope);
}
```

Clear only the selected run's local approval state when the committed detail no longer contains the active approval. Do not clear feedback during an ordinary polling rerender of the same suspension.

- [ ] **Step 5: Render the accessible action-required panel**

Implement `obsApprovalPanel(run)` above `obsDiagnosticStrip`. It must:

- return empty HTML when `obsActiveApproval(run)` is null;
- show `需要审批`, escaped prompt, full escaped `suspensionContent`, and timeout;
- show `批准并继续` and `驳回` only when `obsCanApprove(run)` is true;
- show a labelled textarea and `提交驳回` only after reject is opened;
- disable both decisions while `submitting` or after `accepted`;
- show `审批决定已接受，等待运行状态更新` after `202`;
- show `该 Run 属于其他 scope；管理员观测为只读` for a foreign scope.

Use the existing color variables and button classes. Add only these focused styles: `.approval-panel`, `.approval-head`, `.approval-content`, `.approval-actions`, `.approval-feedback`, and `.diag-strip.info/.problem`. The review content must be scrollable, preformatted, and keyboard-readable.

Insert `obsApprovalPanel(r)` before `obsDiagnosticStrip(r)` in `obsDetail`, so the user's next action precedes diagnostics.

- [ ] **Step 6: Submit through the existing scope-first command route**

Implement:

```javascript
async function obsSubmitApproval(run,approved,rerender){
  var approval=obsActiveApproval(run),state=obsApprovalState(run.id);
  if(!approval||!obsCanApprove(run)||state.submitting||state.accepted) return false;
  var feedback=(state.feedback||'').trim();
  if(!approved&&!feedback){ state.error='请填写驳回反馈'; if(rerender)rerender(); return false; }
  state.submitting=true; state.error=''; if(rerender)rerender();
  var body={stepId:approval.stepId,approved:!!approved};
  if(!approved) body.userInput=feedback;
  try{
    await adminJson('/api/scopes/'+encodeURIComponent(run.scope)+'/runs/'+encodeURIComponent(run.id)+':resume',{
      method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)
    });
    state.accepted=true; state.rejecting=false; return true;
  }catch(e){
    state.error=(e&&(e.body||e.message))||'审批提交失败'; return false;
  }finally{
    state.submitting=false; if(rerender)rerender();
  }
}
```

Do not send `actorId`, `editedContent`, metadata, or a body `scopeId`.

- [ ] **Step 7: Wire click and input behavior**

In `bindObservatory`:

- `obsApprovalApprove` calls `obsSubmitApproval(detail, true, reDetail)`;
- `obsApprovalReject` sets `rejecting = true`, clears the error, and rerenders;
- `obsApprovalRejectCancel` closes the feedback field without dispatch;
- `obsApprovalRejectSubmit` calls `obsSubmitApproval(detail, false, reDetail)`;
- the existing delegated `input` listener stores `data-act="obsApprovalFeedback"` into the selected run's `feedback`.

Keep all commands disabled after `accepted` until the existing poll removes the active approval from committed detail.

- [ ] **Step 8: Make informational diagnostics neutral**

Change `obsDiagnosticStrip` to classify a problem only when the run is terminal-problematic or any diagnostic severity is `warning`/`error`. Render:

```javascript
var problem=bad||(r.diagnostics||[]).some(function(item){
  return item.severity==='warning'||item.severity==='error';
});
var title=problem?'失败诊断':'当前位置';
return '<div class="diag-strip '+(problem?'problem':'info')+'">...';
```

Also change the empty diagnostics-tab copy from `后端暂未返回本次 Run 的失败诊断。` to `后端暂未返回本次 Run 的诊断信息。`

- [ ] **Step 9: Run the focused behavior test and verify GREEN**

Run the command from Step 2. Expected: one passing test, zero failures.

- [ ] **Step 10: Run all Backend Console static-asset tests**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj \
  --nologo \
  --filter 'FullyQualifiedName~BackendConsoleStaticAssetEndpointTests'
```

Expected: all static-asset endpoint tests pass.

- [ ] **Step 11: Commit the owner approval UI slice**

```bash
git add \
  src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html \
  test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs
git commit -m "Add observatory approval actions"
```

---

### Task 4: Run Required Verification and Inspect the Real Surface

**Files:**
- Verify only; no planned production changes.

**Interfaces:**
- Consumes: Tasks 1-3.
- Produces: fresh build, test, guard, and rendered-page evidence.

- [ ] **Step 1: Run the two changed test projects completely**

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
```

Expected: zero failures in all three projects.

- [ ] **Step 2: Run mandatory test and projection guards**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/projection_route_mapping_guard.sh
```

Expected: every guard exits 0.

- [ ] **Step 3: Run architecture and documentation gates**

```bash
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

Expected: both commands exit 0.

- [ ] **Step 4: Build the solution**

```bash
dotnet build aevatar.slnx --nologo
```

Expected: build succeeds with zero errors.

- [ ] **Step 5: Inspect the page against a real owner-scope approval run**

Use the existing production-safe authenticated Browser or local Host surface to open the Run detail. Verify all of the following without inferring completion from the accepted ACK:

- the red false-failure strip is gone for `INFO active_step`;
- the full `daily_tech_digest` YAML appears in `需要审批`;
- `批准并继续` and `驳回` appear for the owner scope;
- a foreign-scope admin view shows the same committed facts but no action buttons;
- approving or rejecting returns an accepted notice, then the existing poll removes the panel after the committed run advances.

Capture a screenshot only if the Browser surface is available; do not add screenshot artifacts to git.

- [ ] **Step 6: Review the final diff for scope and unrelated changes**

```bash
git diff --check HEAD~3..HEAD
git status --short
```

Expected: the implementation commits contain only the files listed in Tasks 1-3. Any pre-existing unrelated working-tree changes remain unstaged and unchanged.
