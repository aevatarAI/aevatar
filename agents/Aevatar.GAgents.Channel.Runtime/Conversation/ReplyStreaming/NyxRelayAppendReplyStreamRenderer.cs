using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>Executes only the immutable operation claimed by the conversation actor.</summary>
internal sealed class NyxRelayAppendReplyStreamRenderer(
    INyxRelayAppendOutboundPort outbound,
    TimeProvider timeProvider,
    TimeSpan operationTimeout) : IReplyOperationStepRenderer
{
    public bool CanHandle(ReplyOperationStepEvent evt) =>
        evt.PayloadCase == ReplyOperationStepEvent.PayloadOneofCase.NyxRelayAppend;

    public async Task ExecuteAsync(IReplyOperationActorContext context, ReplyOperationStepEvent evt, CancellationToken ct)
    {
        if (context is not INyxRelayAppendOperationActorContext appendContext)
            return;
        var step = evt.NyxRelayAppend;
        var operation = await appendContext.ClaimAppendOperationAsync(evt.CorrelationId, step, ct);
        if (operation is null)
            return;

        NyxRelayAppendSendResult result;
        var dispatchBoundaryReached = false;
        try
        {
            // The inbox timeout cannot run while this turn awaits I/O. Bound the entire
            // append attempt, including the response body after ResponseHeadersRead.
            using var deadline = new CancellationTokenSource(operationTimeout, timeProvider);
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            result = await outbound.SendAsync(
                operation.Activity,
                operation.Text,
                operation.MaxLength,
                async dispatchCt =>
                {
                    // Set before persistence: even an ambiguous commit must not allow token replay.
                    dispatchBoundaryReached = true;
                    await appendContext.MarkAppendDispatchedAsync(evt.CorrelationId, step, dispatchCt);
                },
                execution.Token);
        }
        catch (Exception)
        {
            result = new NyxRelayAppendSendResult(
                dispatchBoundaryReached ? NyxRelayAppendSendState.DeliveryUnknown : NyxRelayAppendSendState.PreDispatchFailure,
                ErrorCode: "append_executor_failed");
        }

        // Expiry must not cancel the completion that releases the durable in-flight slot.
        await context.DispatchReplyOperationCompletionAsync(
            new NyxRelayAppendOperationCompletedEvent
            {
                CorrelationId = evt.CorrelationId,
                Kind = step.Kind,
                SegmentIndex = step.SegmentIndex,
                OperationGeneration = step.OperationGeneration,
                RequestDispatched = result.State != NyxRelayAppendSendState.PreDispatchFailure || dispatchBoundaryReached,
                State = result.State switch
                {
                    NyxRelayAppendSendState.Accepted => NyxRelayTextOperationResultState.Succeeded,
                    NyxRelayAppendSendState.DeliveryUnknown => NyxRelayTextOperationResultState.Faulted,
                    _ => NyxRelayTextOperationResultState.Failed,
                },
                RawResult = new NyxRelayTextOperationRawResult
                {
                    PlatformMessageId = result.PlatformMessageId,
                    RawErrorCode = result.ErrorCode,
                    HttpStatus = result.HttpStatus,
                },
            },
            evt.CorrelationId,
            "Nyx relay append",
            ct);
    }
}

internal interface INyxRelayAppendOperationActorContext
{
    Task<NyxRelayAppendExecution?> ClaimAppendOperationAsync(
        string correlationId, NyxRelayAppendOperationStepPayload step, CancellationToken ct);

    Task MarkAppendDispatchedAsync(string correlationId, NyxRelayAppendOperationStepPayload step, CancellationToken ct);
}

internal sealed record NyxRelayAppendExecution(ChatActivity Activity, string Text, int MaxLength);
