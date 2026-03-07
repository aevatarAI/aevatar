using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal delegate Task WorkflowStepRequestHandler(StepRequestEvent request, CancellationToken ct);

internal sealed class WorkflowPrimitiveExecutionPlanner
{
    private readonly Func<StepRequestEvent, CancellationToken, Task<bool>> _tryHandleRegisteredPrimitiveAsync;
    private readonly IReadOnlyDictionary<string, WorkflowStepRequestHandler> _handlersByType;

    public WorkflowPrimitiveExecutionPlanner(
        Func<StepRequestEvent, CancellationToken, Task<bool>> tryHandleRegisteredPrimitiveAsync,
        IReadOnlyDictionary<string, WorkflowStepRequestHandler> handlersByType)
    {
        _tryHandleRegisteredPrimitiveAsync = tryHandleRegisteredPrimitiveAsync
                                             ?? throw new ArgumentNullException(nameof(tryHandleRegisteredPrimitiveAsync));
        _handlersByType = handlersByType ?? throw new ArgumentNullException(nameof(handlersByType));
    }

    public async Task DispatchAsync(StepRequestEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stepType = WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType);
        if (_handlersByType.TryGetValue(stepType, out var handler))
        {
            await handler(request, ct);
            return;
        }

        await _tryHandleRegisteredPrimitiveAsync(request, ct);
    }
}
