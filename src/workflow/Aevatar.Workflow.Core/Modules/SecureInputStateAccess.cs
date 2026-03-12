using System.Linq;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

internal static class SecureInputStateAccess
{
    internal const string ModuleStateKey = "secure_input";

    public static SecureInputModuleState Load(IWorkflowExecutionContext ctx) =>
        WorkflowExecutionStateAccess.Load<SecureInputModuleState>(ctx, ModuleStateKey);

    public static void SetCapturedValue(
        SecureInputModuleState state,
        string? runId,
        string? variable,
        string? value)
    {
        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        var normalizedVariable = NormalizeVariable(variable);
        if (string.IsNullOrWhiteSpace(normalizedRunId) || string.IsNullOrWhiteSpace(normalizedVariable))
            return;

        state.Captured[BuildCapturedKey(normalizedRunId, normalizedVariable)] = new CapturedSecureInputState
        {
            RunId = normalizedRunId,
            VariableName = normalizedVariable,
            Value = value ?? string.Empty,
        };
    }

    public static bool TryGetCapturedValue(
        SecureInputModuleState state,
        string? runId,
        string? variable,
        out string value)
    {
        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        var normalizedVariable = NormalizeVariable(variable);
        if (string.IsNullOrWhiteSpace(normalizedRunId) || string.IsNullOrWhiteSpace(normalizedVariable))
        {
            value = string.Empty;
            return false;
        }

        if (!state.Captured.TryGetValue(BuildCapturedKey(normalizedRunId, normalizedVariable), out var captured))
        {
            value = string.Empty;
            return false;
        }

        value = captured.Value;
        return true;
    }

    public static void RemoveRun(SecureInputModuleState state, string? runId)
    {
        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        if (string.IsNullOrWhiteSpace(normalizedRunId))
            return;

        foreach (var pendingKey in state.Pending
                     .Where(x => string.Equals(x.Value.RunId, normalizedRunId, StringComparison.Ordinal))
                     .Select(x => x.Key)
                     .ToList())
        {
            state.Pending.Remove(pendingKey);
        }

        foreach (var capturedKey in state.Captured
                     .Where(x => string.Equals(x.Value.RunId, normalizedRunId, StringComparison.Ordinal))
                     .Select(x => x.Key)
                     .ToList())
        {
            state.Captured.Remove(capturedKey);
        }
    }

    public static Task SaveAsync(
        SecureInputModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (state.Pending.Count == 0 && state.Captured.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    public static string BuildPendingKey(string runId, string? stepId) =>
        $"{WorkflowRunIdNormalizer.Normalize(runId)}::{stepId ?? string.Empty}";

    private static string BuildCapturedKey(string runId, string variable) =>
        $"{runId}::{variable}";

    private static string NormalizeVariable(string? variable) =>
        string.IsNullOrWhiteSpace(variable) ? string.Empty : variable.Trim();
}
