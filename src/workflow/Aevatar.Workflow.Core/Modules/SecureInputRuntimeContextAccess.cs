namespace Aevatar.Workflow.Core.Modules;

// Refactor (iter115/cluster-3):
//   Old pattern: captured secure input values used a process-local runtime
//                dictionary as the authority.
//   New principle: the compatibility facade reads and writes typed secure input
//                  module state owned by the workflow actor.
internal static class SecureInputRuntimeContextAccess
{
    public static Task SetCapturedValueAsync(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        string? value,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var state = SecureInputStateAccess.Load(ctx);
        SecureInputStateAccess.SetCaptured(state, runId, variable, value);
        return SecureInputStateAccess.SaveAsync(state, ctx, ct);
    }

    public static bool TryGetCapturedValue(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        out string value)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return SecureInputStateAccess.TryGetCaptured(
            SecureInputStateAccess.Load(ctx),
            runId,
            variable,
            out value);
    }

    public static async Task<bool> RemoveCapturedValueAsync(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var state = SecureInputStateAccess.Load(ctx);
        var removed = SecureInputStateAccess.RemoveCaptured(state, runId, variable);
        if (removed)
            await SecureInputStateAccess.SaveAsync(state, ctx, ct);
        return removed;
    }

    public static Task RemoveRunAsync(
        IWorkflowExecutionContext ctx,
        string? runId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var state = SecureInputStateAccess.Load(ctx);
        SecureInputStateAccess.RemoveRun(state, runId);
        return SecureInputStateAccess.SaveAsync(state, ctx, ct);
    }
}
