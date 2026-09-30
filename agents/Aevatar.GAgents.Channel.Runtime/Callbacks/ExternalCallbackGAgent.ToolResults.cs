using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed partial class ExternalCallbackGAgent
{
    private bool ToolDelivery => State.Registration?.LinkDeliveryMode == ExternalCallbackLinkDeliveryMode.Tool;

    protected override async Task OnCommittedStatePublicationRecoveredAsync(EventEnvelope envelope, CancellationToken ct)
    {
        await base.OnCommittedStatePublicationRecoveredAsync(envelope, ct);
        if (!ToolDelivery || Consumed) return;
        await ArmAsync(ct);
        await SendToAsync(Id, new ExternalCallbackStartRequested { CallbackId = State.Registration.CallbackId }, ct);
    }

    [EventHandler]
    public async Task HandleToolResultRequestedAsync(ConnectLinkToolResultRequested command)
    {
        if (!MatchesToolContinuation(command.CallbackId, command.OperationActorId, command.Target) ||
            Consumed || State.ToolResultConsumed)
            return;
        if (!State.ToolResultRequested)
        {
            var state = State.Clone();
            state.ToolResultRequested = true;
            await SaveAsync(state);
        }
        // The original run has committed its waiting state. No actor turn waits
        // for the other actor, and a lost result is covered by the durable retry.
        await ArmAsync();
        await DeliverToolResultAsync();
        if (State.ToolResult is null && !Terminal)
            await SendToAsync(Id, new ExternalCallbackStartRequested { CallbackId = command.CallbackId });
    }

    [EventHandler]
    public async Task HandleToolResultRejectedAsync(ConnectLinkToolResultRejected command)
    {
        if (!MatchesToolContinuation(command.CallbackId, command.OperationActorId, command.Target) || Consumed)
            return;
        var state = State.Clone();
        if (!Terminal)
        {
            state.Result = CallbackResult.Failed;
            state.FailureCode = "tool_continuation_rejected";
            state.ContextRevision++;
        }
        // This closes the local continuation only. Keep any provider result
        // intact: a run deadline cannot prove that NyxID failed to create it.
        state.DeliveryFailureCode = string.IsNullOrWhiteSpace(command.FailureCode)
            ? "tool_continuation_rejected" : command.FailureCode;
        state.ConsumedAtUnixMs = Now;
        state.TimerGeneration++;
        await SaveAsync(state);
    }

    [EventHandler]
    public async Task HandleToolResultConsumedAsync(ConnectLinkToolResultConsumed command)
    {
        if (!MatchesToolContinuation(command.CallbackId, command.OperationActorId, command.Target) ||
            !State.ToolResultRequested || State.ToolResult is null || State.ToolResultConsumed || Consumed)
            return;
        var state = State.Clone();
        state.ToolResultConsumed = true;
        if (state.ToolResult.FailureCode.Length > 0)
        {
            // Creation failure belongs solely to the original tool call. There
            // is no successful browser operation to resume as a business turn.
            state.ConsumedAtUnixMs = Now;
            state.TimerGeneration++;
        }
        await SaveAsync(state);
        if (Terminal && !Consumed) await DeliverAsync();
    }

    private Task DeliverToolResultAsync()
    {
        if (!ToolDelivery || Consumed || State.ToolResultConsumed || State.ToolResult is null || !State.ToolResultRequested)
            return Task.CompletedTask;
        if (State.ToolResult.FailureCode.Length == 0 && !State.LinkContextAccepted && !State.CompletionContextAccepted)
            return DeliverContextAsync();
        return SendToAsync(State.Registration.ToolContinuation.ActorId, new ConnectLinkToolResultProduced
        {
            CallbackId = State.Registration.CallbackId,
            OperationActorId = Id,
            Target = State.Registration.ToolContinuation.Clone(),
            Result = State.ToolResult.Clone(),
        });
    }

    private bool MatchesToolContinuation(string callbackId, string operationActorId, ConnectLinkToolContinuationTarget? target) =>
        ToolDelivery && Matches(callbackId) && operationActorId == Id && target is not null &&
        target.Equals(State.Registration.ToolContinuation) &&
        ActiveInboundEnvelope?.Route?.PublisherActorId == State.Registration.ToolContinuation.ActorId;

    private static bool ValidToolContinuation(ExternalCallbackRegistration registration) =>
        registration.Kind == ExternalCallbackKind.ConnectLink && registration.ToolContinuation is { } target &&
        !string.IsNullOrWhiteSpace(target.ActorId) && !string.IsNullOrWhiteSpace(target.RunId) &&
        target.Attempt > 0 && target.StepIndex > 0 && !string.IsNullOrWhiteSpace(target.CallId) &&
        target.CallId == registration.Origin?.ActionId;
}
