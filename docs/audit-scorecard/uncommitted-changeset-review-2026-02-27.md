# Uncommitted Changeset - Verified Audit Scorecard

**Date**: 2026-02-27  
**Scope**: current uncommitted worktree (verification run)  
**Method**: each reported issue was checked against code; statuses below reflect what is objectively present now.

---

## Verification Summary

- Total findings reviewed: 40
- `CONFIRMED`: 29
- `PARTIALLY_CONFIRMED`: 9
- `NOT_CONFIRMED`: 2

Status definitions:
- `CONFIRMED`: the reported code pattern/problem exists as stated (or very close).
- `PARTIALLY_CONFIRMED`: core concern is valid, but wording/scale/certainty was overstated.
- `NOT_CONFIRMED`: current code does not support the claim as a real issue.

---

## 1. Architecture and Design

- `PARTIALLY_CONFIRMED` `WorkflowGAgent` is a large multi-responsibility class (sub-workflow actor lifecycle, binding cleanup, dynamic reconfiguration, completion routing).  
  Correction: the "grew ~620 lines in this changeset" claim is not supported by current diff stats for this file.
  Location: `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`

- `CONFIRMED` `BuiltInAutoYaml` and `BuiltInAutoReviewYaml` are large embedded YAML literals inside a registry class, and are highly duplicated.
  Location: `src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionRegistry.cs`

- `CONFIRMED` `WorkflowRunBehaviorOptions` exposes mutable `ISet<string>` and `ISet<Type>` with prepopulated defaults.
  Location: `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunBehaviorOptions.cs`

- `CONFIRMED` `LLMCallModule` uses fire-and-forget watchdog startup (`_ = WatchdogAsync(...)`), with potential unobserved task exception risk.
  Location: `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs`

- `CONFIRMED` `HumanApprovalModule` and `HumanInputModule` duplicate `timeout_ms -> timeout_seconds` conversion logic.
  Location: `src/workflow/Aevatar.Workflow.Core/Modules/HumanApprovalModule.cs`, `src/workflow/Aevatar.Workflow.Core/Modules/HumanInputModule.cs`

- `CONFIRMED` `WorkflowParser.RawStep` is very wide, and `LiftRootPrimitiveParameters` is table-like/manual via many `AddIfMissing` calls.
  Location: `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowParser.cs`

---

## 2. Naming and Readability

- `PARTIALLY_CONFIRMED` `ForceConfigureWorkflowAsync` naming concern is subjective but reasonable; XML docs explain behavior.
  Location: `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`

- `CONFIRMED` `wrapAsFallbackTriggerException` in `ConfigureWorkflowForRunAsync` is a boolean behavior switch (boolean trap smell).
  Location: `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs`

- `CONFIRMED` mixed Chinese/English production error strings exist.
  Location: multiple files (notably `WorkflowGAgent`, `WorkflowCallModule`, `WorkflowRunRequestExecutor`)

- `PARTIALLY_CONFIRMED` `ExecuteCoreAsync` naming critique is stylistic; method purpose is still understandable.
  Location: `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowChatRunApplicationService.cs`

- `CONFIRMED` `SanitizeActorSegment` uses per-char LINQ transform (`Select(...).ToArray()`).
  Location: `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`

---

## 3. Method Complexity and Length

- `PARTIALLY_CONFIRMED` `HandleSubWorkflowInvokeRequested` is long and does many things; exact complexity number in the original report is an estimate.
  Location: `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`

- `PARTIALLY_CONFIRMED` `LLMCallModule.HandleAsync` handles 3 event shapes in one method; decomposition concern is valid.  
  Correction: "165+ lines" is overstated for current content.
  Location: `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs`

- `CONFIRMED` `LiftRootPrimitiveParameters` is long and mechanical.
  Location: `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowParser.cs`

- `CONFIRMED` `HandleChat` in `ChatEndpoints` is long and mixes orchestration, error mapping, and response writing.
  Location: `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatEndpoints.cs`

- `PARTIALLY_CONFIRMED` `NormalizeBranches` handles multiple input shapes in one method; maintainability concern is valid though exact line count in the original report is approximate.
  Location: `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowParser.cs`

- `CONFIRMED` `PruneIdleSubWorkflowBindings` has layered condition logic that could be decomposed.
  Location: `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`

---

## 4. Error Handling

- `CONFIRMED` `HandleChat` catch-after-start path writes fallback JSON/SSE with `CancellationToken.None`; secondary write failures can escape.
  Location: `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatEndpoints.cs`

- `CONFIRMED` `TryFinalizeNonSingletonChildAsync` catches all exceptions and logs at Debug level.
  Location: `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`

- `CONFIRMED` `WatchdogAsync` can surface unobserved exceptions because it is started fire-and-forget.
  Location: `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs`

- `CONFIRMED` `TryParseJsonArray` uses bare `catch`.
  Location: `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowParameterValueParser.cs`

- `CONFIRMED` `StopWatchdog` uses bare `catch`.
  Location: `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs`

---

## 5. DRY and Duplication

- `CONFIRMED` `BuiltInAutoYaml` and `BuiltInAutoReviewYaml` are highly similar.
  Location: `src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionRegistry.cs`

- `CONFIRMED` timeout normalization duplication exists in human modules.
  Location: `HumanApprovalModule`, `HumanInputModule`

- `PARTIALLY_CONFIRMED` `ResolveLlmTimeoutMs` exists in both `RoleGAgent` and `LLMCallModule`, but they parse different request surfaces (`metadata` vs step `parameters`), so not pure duplication.
  Location: `src/Aevatar.AI.Core/RoleGAgent.cs`, `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs`

- `CONFIRMED` `NormalizeWorkflowName` helper duplication exists in 3 files.
  Location: `WorkflowRunActorResolver`, `WorkflowDirectFallbackPolicy`, `WorkflowGAgent`

- `CONFIRMED` `Envelope(...)` helper duplication appears across several test files.
  Location: multiple files under `test/`

---

## 6. State Management and Concurrency

- `NOT_CONFIRMED` "`HumanApprovalModule._pending` / `HumanInputModule._pending` are currently unsafe due to concurrent callbacks" is not proven in the current runtime model.  
  Reason: `LocalActor` processes events through a serialized mailbox (`SemaphoreSlim`), and module invocation is serialized per actor. This is still a portability caveat if runtime semantics change, but not a verified live bug now.
  Location: `src/Aevatar.Foundation.Runtime/Actor/LocalActor.cs`, human modules

- `CONFIRMED` pending invocation completion lookup uses O(n) scan via `FirstOrDefault`.
  Location: `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`

- `CONFIRMED` cleanup method builds an intermediate list then iterates again; can be streamlined.
  Location: `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`

---

## 7. Test Quality

- `CONFIRMED` watchdog timeout test is timing-sensitive (`timeout_ms=1` + wait with timeout), so CI fragility risk is real.
  Location: `test/Aevatar.Integration.Tests/WorkflowCoreModulesCoverageTests.cs`

- `NOT_CONFIRMED` "must be added to `test_polling_allowlist`" for `WaitForMessageCountAsync` is not supported by current guard implementation.  
  Reason: guard script checks `Task.Delay(...)` and `WaitUntilAsync(...)`; `WaitForMessageCountAsync` uses `TaskCompletionSource` + `WaitAsync(timeout)` in helper.
  Location: `tools/ci/test_stability_guards.sh`, `test/Aevatar.Foundation.Core.Tests/TestHelper.cs`

- `PARTIALLY_CONFIRMED` `Results.Json` assertions in internal endpoint tests do blend unit/integration style; this is a test pyramid style concern, not a correctness defect.
  Location: `test/Aevatar.Workflow.Host.Api.Tests/ChatEndpointsInternalTests.cs`

- `PARTIALLY_CONFIRMED` no dedicated round-trip negative test from `DynamicWorkflowModule` extraction success to downstream reconfigure failure was found in the module test file; failure handling is covered at actor level.
  Location: `test/Aevatar.Integration.Tests/WorkflowAdditionalModulesCoverageTests.cs`, `test/Aevatar.Integration.Tests/WorkflowGAgentCoverageTests.cs`

---

## 8. Demo Code Quality

- `CONFIRMED` demo `Program.cs` is still very large and monolithic.
  Location: `demos/Aevatar.Demos.Workflow.Web/Program.cs`

- `CONFIRMED` `InMemoryWorkflowDefinitionResolver` is duplicated across two demo projects.
  Location: `demos/Aevatar.Demos.Workflow.Web/InMemoryWorkflowDefinitionResolver.cs`, `demos/Aevatar.Demos.Workflow/InMemoryWorkflowDefinitionResolver.cs`

- `CONFIRMED` demo constants (`AutoResumeDelayMs`, `FinalSseFlushDelayMs`, `WorkflowRunTimeoutMinutes`) are hardcoded and not externally configurable.
  Location: `demos/Aevatar.Demos.Workflow.Web/Program.cs`

---

## 9. Documentation and Consistency

- `CONFIRMED` `BuiltInAutoReviewYaml` uses `name: auto` while registered under key `auto_review`.
  Location: `WorkflowDefinitionRegistry`, `ServiceCollectionExtensions`

- `CONFIRMED` public types `WorkflowDirectFallbackPolicy` and `WorkflowRunBehaviorOptions` have no XML summary docs.
  Location: corresponding files

- `CONFIRMED` `ReconfigureAndExecuteWorkflowEvent` in proto has no explanatory comment.
  Location: `src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto`

---

## 10. Proto and API Surface

No contradictory evidence found for the original positive assessment; additive compatibility direction appears intact.

---

## Corrected Critical List (Top 10)

1. `PARTIALLY_CONFIRMED` `WorkflowGAgent` is oversized and multi-concern; current growth number should be corrected.
2. `CONFIRMED` embedded built-in YAML in registry violates cohesion and duplicates content.
3. `PARTIALLY_CONFIRMED` `LLMCallModule.HandleAsync` is overburdened; current line count claim should be corrected.
4. `CONFIRMED` bare `catch` blocks in parser/watchdog paths should be narrowed or documented.
5. `PARTIALLY_CONFIRMED` timeout parsing overlap exists, but one pair has context-specific semantics.
6. `NOT_CONFIRMED` human module dictionary thread-safety issue is not proven under current serialized mailbox runtime.
7. `CONFIRMED` `NormalizeWorkflowName` duplicated in 3 files.
8. `CONFIRMED` demo resolver class duplicated in 2 projects.
9. `CONFIRMED` `auto_review` registration key vs YAML `name: auto` mismatch exists.
10. `CONFIRMED` watchdog timeout test is timing-sensitive and may be flaky.

---

## Recommended Pre-Merge Focus

- Externalize built-in YAML definitions and remove duplication.
- Split high-load methods/classes (`WorkflowGAgent`, `LLMCallModule.HandleAsync`, parser lift table).
- Tighten exception handling for watchdog and parser utility methods.
- Align workflow naming consistency (`auto_review` key vs YAML name).
- Decide and document runtime assumptions for module state safety (serialized mailbox dependency).
