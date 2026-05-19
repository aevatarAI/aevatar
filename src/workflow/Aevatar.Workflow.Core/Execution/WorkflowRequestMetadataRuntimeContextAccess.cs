using Aevatar.Workflow.Abstractions.Execution;

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
    public static void SetRequestMetadata(
        IWorkflowExecutionStateHost stateHost,
        IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.RuntimeContext.ApplyRequestMetadata(metadata);
    }

    // Refactor (iter16/cluster-031):
    //   Old pattern: request metadata cleanup removed the generic
    //                `workflow.request.metadata` item.
    //   New principle: request metadata cleanup clears the typed LLM,
    //                  connector, and passthrough runtime sections.
    public static void RemoveRequestMetadata(IWorkflowExecutionStateHost stateHost)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
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
