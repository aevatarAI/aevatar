using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

internal static class SecureInputRuntimeItemsAccess
{
    internal const string CapturedItemKey = "secure_input.captured";

    public static void SetCapturedValue(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        string? value)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        var normalizedVariable = NormalizeVariable(variable);
        if (string.IsNullOrWhiteSpace(normalizedRunId) || string.IsNullOrWhiteSpace(normalizedVariable))
            return;

        GetOrCreateCapturedValues(ctx)[BuildCapturedKey(normalizedRunId, normalizedVariable)] = value ?? string.Empty;
    }

    public static bool TryGetCapturedValue(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        out string value)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        var normalizedVariable = NormalizeVariable(variable);
        if (string.IsNullOrWhiteSpace(normalizedRunId) || string.IsNullOrWhiteSpace(normalizedVariable))
        {
            value = string.Empty;
            return false;
        }

        if (!TryGetCapturedValues(ctx, out var capturedValues) ||
            !capturedValues.TryGetValue(BuildCapturedKey(normalizedRunId, normalizedVariable), out value!))
        {
            value = string.Empty;
            return false;
        }

        return true;
    }

    public static bool RemoveCapturedValue(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!TryGetCapturedValues(ctx, out var capturedValues))
            return false;

        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        var normalizedVariable = NormalizeVariable(variable);
        if (string.IsNullOrWhiteSpace(normalizedRunId) || string.IsNullOrWhiteSpace(normalizedVariable))
            return false;

        var removed = capturedValues.Remove(BuildCapturedKey(normalizedRunId, normalizedVariable));
        if (removed && capturedValues.Count == 0)
            WorkflowExecutionItemsAccess.RemoveItem(ctx, CapturedItemKey);

        return removed;
    }

    public static void RemoveRun(
        IWorkflowExecutionContext ctx,
        string? runId)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!TryGetCapturedValues(ctx, out var capturedValues))
            return;

        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        if (string.IsNullOrWhiteSpace(normalizedRunId))
            return;

        foreach (var capturedKey in capturedValues.Keys
                     .Where(x => x.StartsWith($"{normalizedRunId}::", StringComparison.Ordinal))
                     .ToList())
        {
            capturedValues.Remove(capturedKey);
        }

        if (capturedValues.Count == 0)
            WorkflowExecutionItemsAccess.RemoveItem(ctx, CapturedItemKey);
    }

    private static Dictionary<string, string> GetOrCreateCapturedValues(IWorkflowExecutionContext ctx)
    {
        if (TryGetCapturedValues(ctx, out var capturedValues))
            return capturedValues;

        capturedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        WorkflowExecutionItemsAccess.SetItem(ctx, CapturedItemKey, capturedValues);
        return capturedValues;
    }

    private static bool TryGetCapturedValues(
        IWorkflowExecutionContext ctx,
        out Dictionary<string, string> capturedValues)
    {
        if (WorkflowExecutionItemsAccess.TryGetItem(ctx, CapturedItemKey, out Dictionary<string, string>? existing) &&
            existing != null)
        {
            capturedValues = existing;
            return true;
        }

        capturedValues = null!;
        return false;
    }

    private static string BuildCapturedKey(string runId, string variable) =>
        $"{runId}::{variable}";

    private static string NormalizeVariable(string? variable) =>
        string.IsNullOrWhiteSpace(variable) ? string.Empty : variable.Trim();
}
