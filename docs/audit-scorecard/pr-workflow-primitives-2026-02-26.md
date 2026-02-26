# Workflow primitives: fail-fast + run_id normalization + test hardening

## Summary

This PR focuses on workflow primitives reliability and guardrails, with three core outcomes:

1. Add **fail-fast** protection for unknown or unresolvable primitive step types.
2. Unify **`run_id` normalization** across primitive modules.
3. Clean up Reflect module runtime state handling and add focused regression coverage.

## Why

- Prevent workflows from silently hanging when a step type is misspelled or module registration is missing.
- Eliminate cross-module inconsistency caused by mixed `run_id` fallback strategies (`""` vs `"default"`).
- Reduce multi-run maintenance risk in reflection flow and make behavior easier to reason about.

## What Changed

### 1) Fail-fast for unknown primitives

- Extended workflow validation to support known-step-type checks (including `step_type` parameters).
  - `src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs`
- Added primitive catalog helpers for canonical known-type checks.
  - `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowPrimitiveCatalog.cs`
- In `WorkflowGAgent`, compile-time validation now uses registered module names; module installation now fails explicitly when `TryCreate` misses.
  - `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`

### 2) Unified run_id normalization

- Added centralized utility:
  - `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowRunIdNormalizer.cs`
- Applied consistent normalization to workflow primitive modules (key runtime paths), including:
  - `MapReduceModule`, `ParallelFanOutModule`, `RaceModule`, `WhileModule`, `WorkflowCallModule`

### 3) Reflect module cleanup

- Simplified runtime state handling in Reflect flow (keep session-scoped pending mapping only).
  - `src/workflow/Aevatar.Workflow.Core/Modules/ReflectModule.cs`

### 4) Regression tests

- Added validator coverage for unknown primitive checks.
  - `test/Aevatar.Integration.Tests/WorkflowValidatorCoverageTests.cs`
- Added agent coverage for module creation fail-fast behavior.
  - `test/Aevatar.Integration.Tests/WorkflowGAgentCoverageTests.cs`
- Added Reflect concurrency isolation test (`same stepId`, different run).
  - `test/Aevatar.Integration.Tests/WorkflowAdditionalModulesCoverageTests.cs`
- Added unit tests for run_id normalizer.
  - `test/Aevatar.Workflow.Core.Tests/Primitives/WorkflowRunIdNormalizerTests.cs`

### 5) Audit doc update

- Updated scorecard with fix status and latest verification.
  - `docs/audit-scorecard/workflow-primitives-scorecard-2026-02-26.md`

## Behavior Changes

- Workflows with unknown primitive types now fail earlier (validation/install phase) instead of entering potential runtime wait/hang states.
- Missing primitive module registration now surfaces as explicit installation error.
- Empty/whitespace `run_id` is normalized consistently to `default` in updated module paths.

## Test Plan

- `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --filter "FullyQualifiedName~WorkflowParserConfigurationTests|FullyQualifiedName~WorkflowRunIdNormalizerTests" --nologo`
  - Passed: 11/11
- `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "FullyQualifiedName~WorkflowValidatorCoverageTests|FullyQualifiedName~WorkflowGAgentCoverageTests|FullyQualifiedName~WorkflowAdditionalModulesCoverageTests|FullyQualifiedName~WorkflowLoopModuleCoverageTests" --nologo`
  - Passed: 58/58

## Notes

- This PR intentionally keeps `WorkflowLoopModule`’s conservative behavior for ambiguous missing-`run_id` completion routing unchanged.
- `children` semantic alignment remains a follow-up discussion item and is not changed here.

