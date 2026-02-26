# Workflow primitives expansion: closed-world completeness, ChatInput upgrade, HITL, and demos

## Summary

This branch introduces a major workflow capability upgrade against `dev`, centered on:

1. Significant primitives expansion (with closed-world composition and Turing-completeness proof paths).
2. `/api/chat` ChatInput enhancement to support inline `workflowYaml`.
3. While/Loop runtime fixes and run-scoped execution correctness.
4. Human-in-the-loop primitives and protocol support.
5. New workflow demos (CLI + Web) and broad documentation/test coverage.

## Why

- Make workflows expressive enough to support deterministic closed-world orchestration patterns.
- Support both registry workflows and inline ad-hoc workflow YAML via API.
- Improve runtime robustness for concurrent runs, retries/timeouts, branching, and resumable interactions.
- Provide practical demos and reference docs to accelerate adoption.

## Key Changes

### 1) Workflow primitives expansion + closed-world completeness

- Expanded and standardized core primitive module pack (26 registrations including aliases/internal loop):
  - `src/workflow/Aevatar.Workflow.Core/WorkflowCoreModulePack.cs`
- Added new/extended primitive modules such as:
  - `switch`, `race`, `map_reduce`, `evaluate`, `reflect`, `guard`, `delay`, `emit`, `cache`, `wait_signal`, `human_input`, `human_approval`
  - plus strengthened `while`, `foreach`, `parallel`, `workflow_call`, `workflow_loop`
- Introduced primitive catalog + alias canonicalization + closed-world blocked policy:
  - `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowPrimitiveCatalog.cs`
- Added expression engine and expression-driven control/data evaluation:
  - `src/workflow/Aevatar.Workflow.Core/Expressions/WorkflowExpressionEvaluator.cs`
- Added closed-world/Turing-completeness artifacts:
  - `test/Aevatar.Integration.Tests/WorkflowTuringCompletenessTests.cs`
  - `workflows/turing-completeness/counter-addition.yaml`
  - `workflows/turing-completeness/minsky-inc-dec-jz.yaml`
  - `tools/ci/workflow_closed_world_guards.sh`

### 2) `/api/chat` ChatInput and run-start semantics

- `ChatInput` now accepts `workflowYaml`:
  - `src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowCapabilityApiModels.cs`
- Chat run request model upgraded with inline YAML:
  - `src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowChatRunModels.cs`
- API and WebSocket parsing updated accordingly:
  - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatEndpoints.cs`
  - `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatWebSocketCommandParser.cs`
- Resolver now supports inline YAML parse/validate/name-match flow and actor binding rules:
  - `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs`
  - `src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/IWorkflowRunActorPort.cs`
  - `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs`
- Added explicit API errors:
  - `INVALID_WORKFLOW_YAML`
  - `WORKFLOW_NAME_MISMATCH`
  - mapped in `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatRunStartErrorMapper.cs`

### 3) WhileModule / WorkflowLoop runtime fixes

- `WhileModule` refactored to run-scoped state, expression-based continuation, and sub-parameter propagation:
  - `src/workflow/Aevatar.Workflow.Core/Modules/WhileModule.cs`
- `WorkflowLoopModule` strengthened with:
  - run-level correlation (`run_id`)
  - retry / on_error / timeout handling
  - branch-aware next-step routing
  - variable/evaluation integration and closed-world runtime guard
  - `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs`

### 4) Human-in-the-loop workflow support

- Protocol/events upgraded:
  - `run_id` added to core workflow execution events
  - new events: `WorkflowSuspendedEvent`, `WorkflowResumedEvent`, `WaitingForSignalEvent`, `SignalReceivedEvent`
  - `src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto`
- Added HITL modules:
  - `src/workflow/Aevatar.Workflow.Core/Modules/HumanInputModule.cs`
  - `src/workflow/Aevatar.Workflow.Core/Modules/HumanApprovalModule.cs`
  - `src/workflow/Aevatar.Workflow.Core/Modules/WaitSignalModule.cs`
- Projection side support:
  - `src/workflow/Aevatar.Workflow.Projection/Reducers/WorkflowSuspendedEventReducer.cs`

### 5) Workflow demo suite and docs

- New CLI demo project + workflow samples:
  - `demos/Aevatar.Demos.Workflow`
  - includes `01` to `47` YAML workflows covering data/control/composition/HITL cases.
- New Web demo project:
  - `demos/Aevatar.Demos.Workflow.Web` (UI + custom demo modules + interactive execution).
- Major docs additions:
  - `docs/WORKFLOW.md`
  - `docs/WORKFLOW_PRIMITIVES.md`
  - `docs/architecture/workflow-closed-world-turing-completeness.md`

## API Impact

- Request contract change:
  - `POST /api/chat` and WS command payload now support `workflowYaml`.
- Error contract extension:
  - `INVALID_WORKFLOW_YAML`
  - `WORKFLOW_NAME_MISMATCH`
- Workflow execution message schema extended with `run_id` and suspension/signal event types.

## Compatibility Notes

- This is a broad feature branch (large diff vs `dev`), not a narrow patch.
- Runtime behavior is intentionally stricter in workflow validation/start paths when input YAML or workflow binding is invalid.
- For mixed-version deployment, ensure workflow execution proto consumers are upgraded consistently with new fields/events.

## Test Plan

- Targeted module/runtime tests:
  - `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~WorkflowLoopModuleCoverageTests|FullyQualifiedName~WorkflowAdditionalModulesCoverageTests|FullyQualifiedName~WorkflowValidatorCoverageTests|FullyQualifiedName~WorkflowTuringCompletenessTests" --nologo`
- API/run-start behavior tests:
  - `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo`
  - `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo`
- Core primitive/parser/expression tests:
  - `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo`

