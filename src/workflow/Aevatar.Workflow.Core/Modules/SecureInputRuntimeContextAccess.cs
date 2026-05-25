using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;

namespace Aevatar.Workflow.Core.Modules;

// Refactor (iter16/cluster-031):
//   Old pattern: captured secure input values used string-composed keys in the
//                generic execution item bag.
//   New principle: captured secure values use typed run/variable keys in the
//                  actor-owned WorkflowExecutionRuntimeContext.
internal static class SecureInputRuntimeContextAccess
{
    // Refactor (iter16/cluster-031):
    //   Old pattern: secure input capture wrote raw values into generic
    //                execution items using string-composed keys.
    //   New principle: secure input capture writes to typed non-durable
    //                  CapturedSecureInputs in the runtime context.
    public static void SetCapturedValue(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        string? value)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        GetRuntimeContext(ctx).CapturedSecureInputs.Set(runId, variable, value);
    }

    // Refactor (iter16/cluster-031):
    //   Old pattern: connector modules looked up captured secure values through
    //                the generic item accessor.
    //   New principle: connector modules read captured values through typed
    //                  run/variable keys in the runtime context.
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

    // Refactor (iter16/cluster-031):
    //   Old pattern: clearing one secure value removed a string-composed item
    //                key from the generic item bag.
    //   New principle: clearing one secure value removes its typed run/variable
    //                  key from CapturedSecureInputs.
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

    // Refactor (iter16/cluster-031):
    //   Old pattern: run cleanup scanned string-composed secure item keys in
    //                the generic item bag.
    //   New principle: run cleanup removes typed CapturedSecureInputKey entries
    //                  owned by the completed run.
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
