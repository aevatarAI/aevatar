using Aevatar.Workflow.Abstractions.Execution;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowRequestMetadataItemsAccess
{
    public static void SetRequestMetadata(
        IWorkflowExecutionStateHost stateHost,
        IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.RuntimeContext.ApplyRequestMetadata(metadata);
    }

    public static void RemoveRequestMetadata(IWorkflowExecutionStateHost stateHost)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.RuntimeContext.ApplyRequestMetadata(null);
    }

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
