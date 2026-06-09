using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

internal static class WorkflowLlmExecutionIntentRuntimeContextAccess
{
    public static void ApplySenderNyxIdAccessToken(
        IWorkflowExecutionContext ctx,
        WorkflowLlmExecutionIntent intent)
    {
        if (ctx is IWorkflowExecutionRuntimeContextAccessor runtimeAccessor)
            intent.SenderNyxIdAccessToken = Normalize(runtimeAccessor.RuntimeContext.SenderNyxIdAccessToken) ?? string.Empty;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
