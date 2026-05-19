using Aevatar.Workflow.Abstractions.Execution;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowRequestMetadataItemsAccess
{
    // Refactor (iter16/cluster-031):
    //   Old pattern: WorkflowRunGAgent kept Dictionary<string, object?> _executionItems
    //                bag for request metadata, LLM overrides, authorization, secure values
    //   New principle: typed non-durable actor-owned WorkflowExecutionRuntimeContext;
    //                  no facts seam, no proto change
    public static void SetRequestMetadata(
        IWorkflowExecutionStateHost stateHost,
        IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.RuntimeContext.ApplyRequestMetadata(metadata);
    }

    // Refactor (iter16/cluster-031):
    //   Old pattern: WorkflowRunGAgent kept Dictionary<string, object?> _executionItems
    //                bag for request metadata, LLM overrides, authorization, secure values
    //   New principle: typed non-durable actor-owned WorkflowExecutionRuntimeContext;
    //                  no facts seam, no proto change
    public static void RemoveRequestMetadata(IWorkflowExecutionStateHost stateHost)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.RuntimeContext.ApplyRequestMetadata(null);
    }

    // Refactor (iter16/cluster-031):
    //   Old pattern: WorkflowRunGAgent kept Dictionary<string, object?> _executionItems
    //                bag for request metadata, LLM overrides, authorization, secure values
    //   New principle: typed non-durable actor-owned WorkflowExecutionRuntimeContext;
    //                  no facts seam, no proto change
    public static int CopyRequestMetadata(
        IWorkflowExecutionContext ctx,
        IDictionary<string, string> target)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(target);

        if (ctx is not IWorkflowExecutionRuntimeContextAccessor runtimeAccessor)
            return 0;

        var copiedCount = 0;
        foreach (var pair in runtimeAccessor.RuntimeContext.RequestPassthroughMetadata.Values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                continue;

            target[pair.Key] = pair.Value;
            copiedCount++;
        }

        return copiedCount;
    }
}
