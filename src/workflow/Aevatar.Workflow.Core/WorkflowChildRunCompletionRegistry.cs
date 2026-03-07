using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowChildRunCompletionRegistry
{
    private readonly IReadOnlyList<IWorkflowChildRunCompletionHandler> _handlers;

    public WorkflowChildRunCompletionRegistry(IEnumerable<IWorkflowChildRunCompletionHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToArray();
    }

    public async Task<bool> TryHandleAsync(
        WorkflowCompletedEvent evt,
        string? publisherActorId,
        CancellationToken ct)
    {
        foreach (var handler in _handlers)
        {
            if (await handler.TryHandleAsync(evt, publisherActorId, ct))
                return true;
        }

        return false;
    }
}
