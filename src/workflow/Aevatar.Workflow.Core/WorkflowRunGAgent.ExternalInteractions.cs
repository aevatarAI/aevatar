using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent
{
    [EventHandler]
    public async Task HandleWorkflowResumed(WorkflowResumedEvent resumed)
    {
        await _resumeRouter.TryHandleAsync(resumed, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleSignalReceived(SignalReceivedEvent signal)
    {
        await _externalSignalRouter.TryHandleAsync(signal, CancellationToken.None);
    }

    [AllEventHandler(Priority = 5, AllowSelfHandling = true)]
    public async Task HandleRuntimeCallbackEnvelope(EventEnvelope envelope)
    {
        await _internalSignalRouter.TryHandleAsync(envelope, CancellationToken.None);
    }

    [AllEventHandler(Priority = 40, AllowSelfHandling = true)]
    public async Task HandleCompletionEnvelope(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) != true)
            return;

        if (string.Equals(envelope.PublisherId, Id, StringComparison.Ordinal))
            return;

        var completed = envelope.Payload.Unpack<WorkflowCompletedEvent>();
        await _childCompletionRouter.TryHandleAsync(completed, envelope.PublisherId, CancellationToken.None);
    }

    [AllEventHandler(Priority = 30, AllowSelfHandling = true)]
    public async Task HandleRoleAndPromptResponseEnvelope(EventEnvelope envelope)
    {
        await _responseRouter.TryHandleAsync(envelope, defaultPublisherId: Id, CancellationToken.None);
    }
}
