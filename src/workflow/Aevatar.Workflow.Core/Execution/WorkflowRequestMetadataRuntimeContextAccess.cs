using Aevatar.Workflow.Abstractions.Execution;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowRequestMetadataRuntimeContextAccess
{
    public static Task SetRequestMetadataAsync(
        IWorkflowExecutionStateHost stateHost,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        ct.ThrowIfCancellationRequested();
        stateHost.RuntimeContext.ApplyRequestMetadata(metadata);
        return Task.CompletedTask;
    }

    public static Task SetLlmControlAsync(
        IWorkflowExecutionStateHost stateHost,
        WorkflowLlmControlContext? llmControl,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        return WorkflowRunExecutionContextStateAccess.ApplyLlmControlAsync(stateHost, llmControl, ct);
    }

    public static async Task RemoveRequestMetadataAsync(
        IWorkflowExecutionStateHost stateHost,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        await WorkflowRunExecutionContextStateAccess.ClearAsync(stateHost, ct);
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
