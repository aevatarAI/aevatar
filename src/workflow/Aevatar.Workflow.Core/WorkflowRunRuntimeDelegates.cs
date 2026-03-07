using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal delegate Task WorkflowStepDispatchHandler(
    StepDefinition step,
    string input,
    string runId,
    CancellationToken ct);

internal delegate Task WorkflowFinalizeRunHandler(
    bool success,
    string output,
    string error,
    CancellationToken ct);

internal delegate Task WorkflowInternalStepDispatchHandler(
    string runId,
    string parentStepId,
    string stepId,
    string stepType,
    string input,
    string targetRole,
    IReadOnlyDictionary<string, string> parameters,
    CancellationToken ct);

internal delegate Task WorkflowReflectPhaseDispatchHandler(
    string runId,
    WorkflowPendingReflectState state,
    string originalInput,
    CancellationToken ct);
