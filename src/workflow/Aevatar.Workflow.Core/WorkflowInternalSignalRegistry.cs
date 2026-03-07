using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowInternalSignalRegistry
{
    private readonly IReadOnlyList<IWorkflowInternalSignalHandler> _handlers;

    public WorkflowInternalSignalRegistry(IEnumerable<IWorkflowInternalSignalHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToArray();
    }

    public async Task<bool> TryHandleAsync(EventEnvelope envelope, CancellationToken ct)
    {
        foreach (var handler in _handlers)
        {
            if (!handler.CanHandle(envelope))
                continue;

            await handler.HandleAsync(envelope, ct);
            return true;
        }

        return false;
    }
}
