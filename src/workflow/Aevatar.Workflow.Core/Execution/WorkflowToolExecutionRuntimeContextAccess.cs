using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions.Execution;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowToolExecutionRuntimeContextAccess
{
    public static void SetToolContext(
        IWorkflowExecutionStateHost stateHost,
        AgentToolExecutionContext? toolContext)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.RuntimeContext.ApplyToolContext(toolContext);
    }

    public static AgentToolExecutionContext? GetToolContext(IWorkflowExecutionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return ctx is IWorkflowExecutionRuntimeContextAccessor runtimeAccessor
            ? runtimeAccessor.RuntimeContext.ToolContext
            : null;
    }
}
