using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.Workflow.Core.Execution;

// Refactor (iter16/cluster-031):
//   Old pattern: request metadata was copied into the generic execution item
//                bag as `workflow.request.metadata`, mixing control values with passthrough metadata.
//   New principle: request metadata is filtered into same-turn passthrough metadata;
//                  connector authorization and LLM routing use typed runtime sections.
internal static class WorkflowRequestMetadataRuntimeContextAccess
{
    // Refactor (iter16/cluster-031):
    //   Old pattern: request metadata writes stored a normalized dictionary
    //                under `workflow.request.metadata` in the item bag.
    //   New principle: request metadata writes keep only same-turn passthrough values;
    //                  typed connector and LLM state are updated through explicit typed APIs.
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

    public static Task SetToolContextAsync(
        IWorkflowExecutionStateHost stateHost,
        AgentToolExecutionContext? toolContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        return WorkflowRunExecutionContextStateAccess.ApplyToolContextAsync(stateHost, toolContext, ct);
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
