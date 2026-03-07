using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowAsyncOperationReconciler
{
    private readonly WorkflowStatefulCompletionHandlerRegistry _statefulCompletionHandlers;
    private readonly WorkflowInternalSignalRegistry _internalSignalHandlers;
    private readonly WorkflowResponseHandlerRegistry _responseHandlers;

    public WorkflowAsyncOperationReconciler(
        WorkflowStatefulCompletionHandlerRegistry statefulCompletionHandlers,
        WorkflowInternalSignalRegistry internalSignalHandlers,
        WorkflowResponseHandlerRegistry responseHandlers)
    {
        _statefulCompletionHandlers = statefulCompletionHandlers ?? throw new ArgumentNullException(nameof(statefulCompletionHandlers));
        _internalSignalHandlers = internalSignalHandlers ?? throw new ArgumentNullException(nameof(internalSignalHandlers));
        _responseHandlers = responseHandlers ?? throw new ArgumentNullException(nameof(responseHandlers));
    }

    public async Task<bool> TryHandleStatefulCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
        => await _statefulCompletionHandlers.TryHandleAsync(evt, ct);

    public async Task HandleRuntimeCallbackEnvelopeAsync(EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await _internalSignalHandlers.TryHandleAsync(envelope, ct);
    }

    public async Task HandleRoleAndPromptResponseEnvelopeAsync(
        EventEnvelope envelope,
        string defaultPublisherId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await _responseHandlers.TryHandleAsync(envelope, defaultPublisherId, ct);
    }
}
