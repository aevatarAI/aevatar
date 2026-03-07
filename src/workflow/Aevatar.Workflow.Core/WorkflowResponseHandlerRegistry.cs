using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowResponseHandlerRegistry
{
    private readonly IReadOnlyList<IWorkflowResponseHandler> _handlers;

    public WorkflowResponseHandlerRegistry(IEnumerable<IWorkflowResponseHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToArray();
    }

    public async Task<bool> TryHandleAsync(EventEnvelope envelope, string defaultPublisherId, CancellationToken ct)
    {
        foreach (var handler in _handlers)
        {
            if (await handler.TryHandleAsync(envelope, defaultPublisherId, ct))
                return true;
        }

        return false;
    }
}
