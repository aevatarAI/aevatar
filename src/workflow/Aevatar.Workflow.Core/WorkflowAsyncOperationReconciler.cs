using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core;

internal delegate Task<bool> WorkflowStatefulCompletionHandler(StepCompletedEvent evt, CancellationToken ct);

internal sealed class WorkflowAsyncOperationReconciler
{
    private readonly IReadOnlyList<WorkflowStatefulCompletionHandler> _statefulCompletionHandlers;
    private readonly Func<WorkflowStepTimeoutFiredEvent, EventEnvelope, CancellationToken, Task> _workflowStepTimeoutHandler;
    private readonly Func<WorkflowStepRetryBackoffFiredEvent, EventEnvelope, CancellationToken, Task> _workflowStepRetryBackoffHandler;
    private readonly Func<DelayStepTimeoutFiredEvent, EventEnvelope, CancellationToken, Task> _delayTimeoutHandler;
    private readonly Func<WaitSignalTimeoutFiredEvent, EventEnvelope, CancellationToken, Task> _waitSignalTimeoutHandler;
    private readonly Func<LlmCallWatchdogTimeoutFiredEvent, EventEnvelope, CancellationToken, Task> _llmWatchdogTimeoutHandler;
    private readonly Func<string?, string, string, CancellationToken, Task> _llmLikeResponseHandler;

    public WorkflowAsyncOperationReconciler(
        IReadOnlyList<WorkflowStatefulCompletionHandler> statefulCompletionHandlers,
        Func<WorkflowStepTimeoutFiredEvent, EventEnvelope, CancellationToken, Task> workflowStepTimeoutHandler,
        Func<WorkflowStepRetryBackoffFiredEvent, EventEnvelope, CancellationToken, Task> workflowStepRetryBackoffHandler,
        Func<DelayStepTimeoutFiredEvent, EventEnvelope, CancellationToken, Task> delayTimeoutHandler,
        Func<WaitSignalTimeoutFiredEvent, EventEnvelope, CancellationToken, Task> waitSignalTimeoutHandler,
        Func<LlmCallWatchdogTimeoutFiredEvent, EventEnvelope, CancellationToken, Task> llmWatchdogTimeoutHandler,
        Func<string?, string, string, CancellationToken, Task> llmLikeResponseHandler)
    {
        _statefulCompletionHandlers = statefulCompletionHandlers ?? throw new ArgumentNullException(nameof(statefulCompletionHandlers));
        _workflowStepTimeoutHandler = workflowStepTimeoutHandler ?? throw new ArgumentNullException(nameof(workflowStepTimeoutHandler));
        _workflowStepRetryBackoffHandler = workflowStepRetryBackoffHandler ?? throw new ArgumentNullException(nameof(workflowStepRetryBackoffHandler));
        _delayTimeoutHandler = delayTimeoutHandler ?? throw new ArgumentNullException(nameof(delayTimeoutHandler));
        _waitSignalTimeoutHandler = waitSignalTimeoutHandler ?? throw new ArgumentNullException(nameof(waitSignalTimeoutHandler));
        _llmWatchdogTimeoutHandler = llmWatchdogTimeoutHandler ?? throw new ArgumentNullException(nameof(llmWatchdogTimeoutHandler));
        _llmLikeResponseHandler = llmLikeResponseHandler ?? throw new ArgumentNullException(nameof(llmLikeResponseHandler));
    }

    public async Task<bool> TryHandleStatefulCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        foreach (var handler in _statefulCompletionHandlers)
        {
            if (await handler(evt, ct))
                return true;
        }

        return false;
    }

    public async Task HandleRuntimeCallbackEnvelopeAsync(EventEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (payload.Is(WorkflowStepTimeoutFiredEvent.Descriptor))
        {
            await _workflowStepTimeoutHandler(payload.Unpack<WorkflowStepTimeoutFiredEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(WorkflowStepRetryBackoffFiredEvent.Descriptor))
        {
            await _workflowStepRetryBackoffHandler(payload.Unpack<WorkflowStepRetryBackoffFiredEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(DelayStepTimeoutFiredEvent.Descriptor))
        {
            await _delayTimeoutHandler(payload.Unpack<DelayStepTimeoutFiredEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(WaitSignalTimeoutFiredEvent.Descriptor))
        {
            await _waitSignalTimeoutHandler(payload.Unpack<WaitSignalTimeoutFiredEvent>(), envelope, ct);
            return;
        }

        if (payload.Is(LlmCallWatchdogTimeoutFiredEvent.Descriptor))
            await _llmWatchdogTimeoutHandler(payload.Unpack<LlmCallWatchdogTimeoutFiredEvent>(), envelope, ct);
    }

    public async Task HandleRoleAndPromptResponseEnvelopeAsync(
        EventEnvelope envelope,
        string defaultPublisherId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();
            await _llmLikeResponseHandler(
                evt.SessionId,
                evt.Content ?? string.Empty,
                envelope.PublisherId,
                ct);
            return;
        }

        if (payload.Is(ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<ChatResponseEvent>();
            await _llmLikeResponseHandler(
                evt.SessionId,
                evt.Content ?? string.Empty,
                string.IsNullOrWhiteSpace(envelope.PublisherId) ? defaultPublisherId : envelope.PublisherId,
                ct);
        }
    }
}
