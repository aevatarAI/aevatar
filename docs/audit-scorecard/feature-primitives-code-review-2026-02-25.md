# Code Review Report: `feature/primitives` (base: `origin/dev`)

## Findings

### 1) [Critical] Protobuf field renumbering breaks wire compatibility

`workflow_execution_messages.proto` changed existing field numbers when introducing `run_id` (instead of appending new fields with new tags).  
This is a wire-level breaking change for any mixed-version deployment, persisted envelopes, replay streams, and inter-service/event-bus consumers.

- **Current branch**: `input/parameters/success/output/error/...` field tags are shifted.
- **Base branch (`origin/dev`)**: those tags had different numbers.
- **Impact**: old producers/new consumers (or reverse) will deserialize wrong fields silently, not just fail-fast.

**Evidence** (`src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto`):

```5:10:src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto
message StartWorkflowEvent    { string workflow_name = 1; string run_id = 2; string input = 3; map<string, string> parameters = 4; }
message WorkflowCompletedEvent { string workflow_name = 1; string run_id = 2; bool success = 3; string output = 4; string error = 5; }
message StepRequestEvent      { string step_id = 1; string step_type = 2; string run_id = 3; string input = 4; string target_role = 5; map<string, string> parameters = 6; }
message StepCompletedEvent    { string step_id = 1; string run_id = 2; bool success = 3; string output = 4; string error = 5; string worker_id = 6; map<string, string> metadata = 7; }
message WorkflowSuspendedEvent { string run_id = 1; string step_id = 2; string suspension_type = 3; string prompt = 4; int32 timeout_seconds = 5; map<string, string> metadata = 6; }
message WorkflowResumedEvent   { string run_id = 1; string step_id = 2; bool approved = 3; string user_input = 4; map<string, string> metadata = 5; }
```

**Recommendation**

- Keep old tags unchanged; append `run_id` using new, previously unused field numbers.
- If incompatibility is intentional, explicitly version messages (e.g. `StepRequestEventV2`) and isolate routing/consumers by version.

---

### 2) [High] Multi-run routing is inconsistent because most modules do not propagate `RunId`

`WorkflowLoopModule` now supports concurrent runs and dispatches `StepRequestEvent.RunId`, but it still needs fallback resolution for `StepCompletedEvent` without `RunId` (by `StepId -> runId` map).  
That fallback is unsafe when multiple runs execute the same step id (common case: all runs start from `s1`).

**Evidence A: loop relies on `StepId -> RunId` fallback**

```323:331:src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs
private string ResolveRunId(StepCompletedEvent evt)
{
    if (!string.IsNullOrWhiteSpace(evt.RunId))
        return evt.RunId;

    if (!string.IsNullOrWhiteSpace(evt.StepId) && _stepToRunId.TryGetValue(evt.StepId, out var runId))
        return runId;

    return _activeRunIds.Count == 1 ? _activeRunIds.First() : string.Empty;
}
```

```269:271:src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs
_stepToRunId[step.Id] = runId;
StartTimeout(step, runId, ctx, ct);
await ctx.PublishAsync(req, EventDirection.Self, ct);
```

**Evidence B: modules emit `StepCompletedEvent` without `RunId` even though `StepRequestEvent` has it**

```36:39:src/workflow/Aevatar.Workflow.Core/Modules/GuardModule.cs
await ctx.PublishAsync(new StepCompletedEvent
{
    StepId = request.StepId, Success = true, Output = input,
}, EventDirection.Self, ct);
```

```100:105:src/workflow/Aevatar.Workflow.Core/Modules/EvaluateModule.cs
var completed = new StepCompletedEvent
{
    StepId = evalCtx.StepId,
    Success = true,
    Output = evalCtx.OriginalInput,
};
```

```105:110:src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs
await ctx.PublishAsync(new StepCompletedEvent
{
    StepId = pending.StepId,
    Success = true, Output = evt.Content ?? "",
    WorkerId = envelope.PublisherId,
}, EventDirection.Self, ct);
```

**Impact**

- Wrong run can be advanced/completed by another run’s result.
- One run can stall/hang because its completion is consumed by a sibling run.
- Timeout/retry branches can amplify this misrouting.

**Recommendation**

- Make `RunId` mandatory in all module-emitted `StepCompletedEvent`.
- Remove `StepId -> runId` fallback path once propagation is complete.
- Add guardrails: reject or dead-letter any completion without `RunId` when `activeRuns > 1`.

---

### 3) [Medium] Session key design can collide across runs/retries

LLM/evaluate modules key pending contexts by `ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, stepId)` (no `RunId`, no attempt index).  
With concurrent runs or late responses after retry, a stale response can match a new in-flight request.

**Evidence**

```57:59:src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs
// Use per-step session id to avoid collisions across concurrent llm_call steps.
var chatSessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, request.StepId);
_pending[chatSessionId] = request;
```

```53:54:src/workflow/Aevatar.Workflow.Core/Modules/EvaluateModule.cs
var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(ctx.AgentId, request.StepId);
_pending[sessionId] = new EvalContext(request.StepId, request.Input ?? "", threshold, onBelow);
```

```8:16:src/Aevatar.AI.Abstractions/ChatSessionKeys.cs
public static string CreateWorkflowStepSessionId(string scopeId, string stepId)
{
    if (string.IsNullOrWhiteSpace(scopeId))
        throw new ArgumentException("ScopeId is required.", nameof(scopeId));
    if (string.IsNullOrWhiteSpace(stepId))
        throw new ArgumentException("StepId is required.", nameof(stepId));

    return $"{scopeId}:{stepId}";
}
```

**Recommendation**

- Include `run_id` (and ideally attempt id) in session id generation.
- Keep pending dictionaries keyed by `(runId, stepId, attempt)` semantic key.

## Open Questions / Assumptions

- Is wire compatibility across rolling upgrades and replayed historical events a hard requirement for this repo’s workflow protocol?
- Is concurrent execution of multiple runs per workflow actor an explicit product requirement (current implementation suggests yes)?

## Validation Performed

- Diff basis: `origin/dev...feature/primitives`.
- Commands run:
  - `git diff --stat origin/dev...feature/primitives`
  - `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~WorkflowLoopModuleCoverageTests|FullyQualifiedName~WorkflowCoreModulesCoverageTests"`
- Test result: **Passed (38/38)**.  
  Existing tests do not cover the multi-run + missing-`RunId` misrouting path above.

## Overall Assessment

**Request changes** before merge, mainly for protocol compatibility and run-correlation correctness.  
These two issues are architectural/runtime correctness risks, not style-level concerns.
