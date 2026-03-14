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
    }

    public static Task SaveAsync(
        SecureInputModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        state.Captured.Clear();
        if (state.Pending.Count == 0)
            return WorkflowExecutionStateAccess.ClearAsync(ctx, ModuleStateKey, ct);

        return WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);
    }

    public static string BuildPendingKey(string runId, string? stepId) =>
        $"{WorkflowRunIdNormalizer.Normalize(runId)}::{stepId ?? string.Empty}";
}
