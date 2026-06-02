using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.Workflow.Core.Execution;

// Refactor (iter16/cluster-031):
//   Old pattern: request metadata was copied into the generic execution item
//                bag as `workflow.request.metadata`, mixing control values with passthrough metadata.
//   New principle: request metadata is normalized into typed runtime sections
//                  for LLM overrides, connector authorization, and filtered passthrough metadata.
internal static class WorkflowRequestMetadataRuntimeContextAccess
{
    // Refactor (iter16/cluster-031):
    //   Old pattern: request metadata writes stored a normalized dictionary
    //                under `workflow.request.metadata` in the item bag.
    //   New principle: request metadata writes promote known control keys into
    //                  typed runtime sections and keep only passthrough values.
    public static async Task SetRequestMetadataAsync(
        IWorkflowExecutionStateHost stateHost,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        await WorkflowRunExecutionContextStateAccess.ApplyRequestMetadataAsync(stateHost, metadata, ct);
        stateHost.RuntimeContext.ApplyRequestMetadata(metadata);
    }

    public static Task SetToolContextAsync(
        IWorkflowExecutionStateHost stateHost,
        AgentToolExecutionContext? toolContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        return WorkflowRunExecutionContextStateAccess.ApplyToolContextAsync(stateHost, toolContext, ct);
    }

    // Refactor (iter16/cluster-031):
    //   Old pattern: request metadata cleanup removed the generic
    //                `workflow.request.metadata` item.
    //   New principle: request metadata cleanup clears the typed LLM,
    //                  connector, and passthrough runtime sections.
    public static async Task RemoveRequestMetadataAsync(
        IWorkflowExecutionStateHost stateHost,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        await WorkflowRunExecutionContextStateAccess.ClearAsync(stateHost, ct);
        stateHost.RuntimeContext.ApplyRequestMetadata(null);
    }

    // Refactor (iter16/cluster-031):
    //   Old pattern: LLM calls copied request metadata out of the generic item
    //                bag, where control keys and passthrough keys were mixed.
    //   New principle: LLM calls copy only the filtered passthrough runtime
    //                  metadata section.
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
