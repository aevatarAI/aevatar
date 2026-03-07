using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowPrimitiveExecutionPlanner
{
    private readonly Func<StepRequestEvent, CancellationToken, Task<bool>> _tryHandleRegisteredPrimitiveAsync;
    private readonly WorkflowStepFamilyDispatchTable _dispatchTable;

    public WorkflowPrimitiveExecutionPlanner(
        Func<StepRequestEvent, CancellationToken, Task<bool>> tryHandleRegisteredPrimitiveAsync,
        WorkflowStepFamilyDispatchTable dispatchTable)
    {
        _tryHandleRegisteredPrimitiveAsync = tryHandleRegisteredPrimitiveAsync
                                             ?? throw new ArgumentNullException(nameof(tryHandleRegisteredPrimitiveAsync));
        _dispatchTable = dispatchTable ?? throw new ArgumentNullException(nameof(dispatchTable));
    }

    public async Task DispatchAsync(StepRequestEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stepType = WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType);
        if (_dispatchTable.TryGet(stepType, out var handler))
        {
            await handler.HandleStepRequestAsync(request, ct);
            return;
        }

        await _tryHandleRegisteredPrimitiveAsync(request, ct);
    }
}
