using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent
{
    [EventHandler]
    public async Task HandleWorkflowResumed(WorkflowResumedEvent resumed)
    {
        if (!string.Equals(WorkflowRunIdNormalizer.Normalize(resumed.RunId), State.RunId, StringComparison.Ordinal))
            return;

        if (!WorkflowRunSupport.TryResolvePendingHumanGate(State, resumed.ResumeToken, out var pending))
            return;

        var next = State.Clone();
        next.PendingHumanGates.Remove(pending.StepId);
        next.Status = StatusActive;
        await PersistStateAsync(next, CancellationToken.None);

        if (string.Equals(pending.GateType, "human_input", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(resumed.UserInput) && !resumed.Approved)
            {
                var onTimeout = string.IsNullOrWhiteSpace(pending.OnTimeout) ? "fail" : pending.OnTimeout;
                await PublishAsync(new StepCompletedEvent
                {
                    StepId = pending.StepId,
                    RunId = State.RunId,
                    Success = !string.Equals(onTimeout, "fail", StringComparison.OrdinalIgnoreCase),
                    Output = pending.Input,
                    Error = string.Equals(onTimeout, "fail", StringComparison.OrdinalIgnoreCase)
                        ? "Human input timed out"
                        : string.Empty,
                }, EventDirection.Self, CancellationToken.None);
                return;
            }

            await PublishAsync(new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = State.RunId,
                Success = true,
                Output = resumed.UserInput ?? string.Empty,
            }, EventDirection.Self, CancellationToken.None);
            return;
        }

        var rejected = !resumed.Approved;
        var rejectionOutput = !string.IsNullOrEmpty(resumed.UserInput)
            ? $"[Previous content]\n{pending.Input}\n\n[User feedback]\n{resumed.UserInput}"
            : pending.Input;

        var completed = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = State.RunId,
            Success = !rejected || !string.Equals(pending.OnReject, "fail", StringComparison.OrdinalIgnoreCase),
            Output = rejected ? rejectionOutput : string.IsNullOrEmpty(resumed.UserInput) ? pending.Input : resumed.UserInput,
            Error = rejected && string.Equals(pending.OnReject, "fail", StringComparison.OrdinalIgnoreCase)
                ? "Human approval rejected"
                : string.Empty,
        };
        completed.Metadata["branch"] = resumed.Approved ? "true" : "false";
        await PublishAsync(completed, EventDirection.Self, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleSignalReceived(SignalReceivedEvent signal)
    {
        if (!string.Equals(WorkflowRunIdNormalizer.Normalize(signal.RunId), State.RunId, StringComparison.Ordinal))
            return;

        if (!WorkflowRunSupport.TryResolvePendingSignalWait(State, signal.WaitToken, out var pending))
            return;

        var next = State.Clone();
        next.PendingSignalWaits.Remove(pending.StepId);
        next.Status = StatusActive;
        await PersistStateAsync(next, CancellationToken.None);

        await PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = State.RunId,
            Success = true,
            Output = string.IsNullOrEmpty(signal.Payload) ? pending.Input : signal.Payload,
        }, EventDirection.Self, CancellationToken.None);
    }

    [AllEventHandler(Priority = 5, AllowSelfHandling = true)]
    public async Task HandleRuntimeCallbackEnvelope(EventEnvelope envelope)
    {
        await _asyncOperationReconciler.HandleRuntimeCallbackEnvelopeAsync(envelope, CancellationToken.None);
    }

    [AllEventHandler(Priority = 40, AllowSelfHandling = true)]
    public async Task HandleCompletionEnvelope(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) != true)
            return;

        if (string.Equals(envelope.PublisherId, Id, StringComparison.Ordinal))
            return;

        var completed = envelope.Payload.Unpack<WorkflowCompletedEvent>();
        await _compositionRuntime.TryHandleSubWorkflowCompletionAsync(completed, envelope.PublisherId, CancellationToken.None);
    }

    [AllEventHandler(Priority = 30, AllowSelfHandling = true)]
    public async Task HandleRoleAndPromptResponseEnvelope(EventEnvelope envelope)
    {
        await _asyncOperationReconciler.HandleRoleAndPromptResponseEnvelopeAsync(
            envelope,
            defaultPublisherId: Id,
            CancellationToken.None);
    }
}
