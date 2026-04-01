using Aevatar.Workflow.Abstractions.Execution;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowRequestMetadataItemsAccess
{
    private const string RequestMetadataItemKey = "workflow.request.metadata";

    public static void SetRequestMetadata(
        IWorkflowExecutionStateHost stateHost,
        IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentNullException.ThrowIfNull(stateHost);

        if (metadata == null || metadata.Count == 0)
        {
            stateHost.RemoveExecutionItem(RequestMetadataItemKey);
            return;
        }

        var copied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                continue;

            copied[pair.Key.Trim()] = pair.Value.Trim();
        }

        if (copied.Count == 0)
        {
            stateHost.RemoveExecutionItem(RequestMetadataItemKey);
            return;
        }

        stateHost.SetExecutionItem(RequestMetadataItemKey, copied);
    }

    public static void RemoveRequestMetadata(IWorkflowExecutionStateHost stateHost)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        stateHost.RemoveExecutionItem(RequestMetadataItemKey);
    }

    public static int CopyRequestMetadata(
        IWorkflowExecutionContext ctx,
        IDictionary<string, string> target)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(target);

        if (!WorkflowExecutionItemsAccess.TryGetItem(
                ctx,
                RequestMetadataItemKey,
                out Dictionary<string, string>? stored) ||
            stored == null ||
            stored.Count == 0)
        {
            return 0;
        }

        var copiedCount = 0;
        foreach (var pair in stored)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                continue;

            target[pair.Key] = pair.Value;
            copiedCount++;
        }

        return copiedCount;
    }
}
