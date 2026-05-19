using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;

namespace Aevatar.Workflow.Core.Modules;

internal static class SecureInputRuntimeItemsAccess
{
    public static void SetCapturedValue(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        string? value)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        GetRuntimeContext(ctx).CapturedSecureInputs.Set(runId, variable, value);
    }

    public static bool TryGetCapturedValue(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        out string value)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx is not IWorkflowExecutionRuntimeContextAccessor runtimeAccessor)
        {
            value = string.Empty;
            return false;
        }

        return runtimeAccessor.RuntimeContext.CapturedSecureInputs.TryGet(runId, variable, out value);
    }

    public static bool RemoveCapturedValue(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx is not IWorkflowExecutionRuntimeContextAccessor runtimeAccessor)
            return false;

        return runtimeAccessor.RuntimeContext.CapturedSecureInputs.Remove(runId, variable);
    }

    public static void RemoveRun(
        IWorkflowExecutionContext ctx,
        string? runId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx is not IWorkflowExecutionRuntimeContextAccessor runtimeAccessor)
            return;

        runtimeAccessor.RuntimeContext.CapturedSecureInputs.RemoveRun(runId);
    }

    private static WorkflowExecutionRuntimeContext GetRuntimeContext(IWorkflowExecutionContext ctx)
    {
        if (ctx is IWorkflowExecutionRuntimeContextAccessor runtimeAccessor)
            return runtimeAccessor.RuntimeContext;

        throw new InvalidOperationException(
            $"Workflow execution context `{ctx.GetType().FullName}` does not support actor-owned runtime context.");
    }
}
