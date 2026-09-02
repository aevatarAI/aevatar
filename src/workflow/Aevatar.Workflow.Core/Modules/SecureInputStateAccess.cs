using Aevatar.Foundation.Abstractions.Credentials;
using System.Linq;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

internal static class SecureInputStateAccess
{
    internal const string ModuleStateKey = "secure_input";

    public static SecureInputModuleState Load(IWorkflowExecutionContext ctx) =>
        WorkflowExecutionStateAccess.Load<SecureInputModuleState>(ctx, ModuleStateKey);

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

    public static void SetCaptured(
        SecureInputModuleState state,
        string? runId,
        string? variable,
        string? value)
    {
        if (!TryBuildCapturedKey(runId, variable, out var key, out var normalizedRunId, out var normalizedVariable))
            return;

        state.Captured[key] = new CapturedSecureInputState
        {
            RunId = normalizedRunId,
            VariableName = normalizedVariable,
            Value = value ?? string.Empty,
        };
    }

    public static void SetCapturedReference(
        SecureInputModuleState state,
        string? runId,
        string? variable,
        RuntimeSecretReference? valueReference)
    {
        if (!TryBuildCapturedKey(runId, variable, out var key, out var normalizedRunId, out var normalizedVariable))
            return;

        state.Captured[key] = new CapturedSecureInputState
        {
            RunId = normalizedRunId,
            VariableName = normalizedVariable,
            Value = string.Empty,
            ValueReference = valueReference?.Clone(),
        };
    }

    public static bool TryGetCaptured(
        SecureInputModuleState state,
        string? runId,
        string? variable,
        out string value)
    {
        if (TryBuildCapturedKey(runId, variable, out var key, out _, out _) &&
            state.Captured.TryGetValue(key, out var captured))
        {
            if (!string.IsNullOrWhiteSpace(captured.ValueReference?.Ref))
            {
                value = string.Empty;
                return false;
            }

            var legacyValue = captured.Value ?? string.Empty;
            if (!string.IsNullOrEmpty(legacyValue))
            {
                value = legacyValue;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    public static bool RemoveCaptured(
        SecureInputModuleState state,
        string? runId,
        string? variable)
    {
        if (!TryBuildCapturedKey(runId, variable, out var key, out _, out _))
            return false;

        return state.Captured.Remove(key);
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

    private static bool TryBuildCapturedKey(
        string? runId,
        string? variable,
        out string key,
        out string normalizedRunId,
        out string normalizedVariable)
    {
        normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        normalizedVariable = string.IsNullOrWhiteSpace(variable) ? string.Empty : variable.Trim();
        if (string.IsNullOrWhiteSpace(normalizedRunId) ||
            string.IsNullOrWhiteSpace(normalizedVariable))
        {
            key = string.Empty;
            return false;
        }

        key = $"{normalizedRunId}::{normalizedVariable}";
        return true;
    }
}
