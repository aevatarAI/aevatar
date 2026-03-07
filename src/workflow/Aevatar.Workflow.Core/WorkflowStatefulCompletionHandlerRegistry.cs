using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowStatefulCompletionHandlerRegistry
{
    private readonly IReadOnlyList<IWorkflowStatefulCompletionHandler> _handlers;

    public WorkflowStatefulCompletionHandlerRegistry(IEnumerable<IWorkflowStatefulCompletionHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToArray();
    }

    public async Task<bool> TryHandleAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        foreach (var handler in _handlers)
        {
            if (await handler.TryHandleCompletionAsync(evt, ct))
                return true;
        }

        return false;
    }
}
