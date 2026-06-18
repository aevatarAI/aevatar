using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed class LarkCardReplyStreamRenderer(
    IConversationCardTurnRunner runner,
    ILogger<LarkCardReplyStreamRenderer> logger) : ILarkCardReplyStreamRenderer
{
    public bool CanHandle(ReplyOperationStepEvent evt) =>
        evt.PayloadCase == ReplyOperationStepEvent.PayloadOneofCase.LarkCard;

    public ReplyOperationStepEvent CreateCreateStep(LarkCardCreateOperationStepInput input)
    {
        var operationId = LarkCardReplyOperationIds.BuildOperationId(
            input.CorrelationId,
            LarkCardOperationPhase.Create,
            input.Sequence,
            input.Generation);
        return new ReplyOperationStepEvent
        {
            OperationId = operationId,
            OperationName = "lark-card-create",
            CorrelationId = input.CorrelationId,
            LeaseEpoch = input.Generation,
            LarkCard = new LarkCardOperationStepPayload
            {
                Operation = LarkCardOperationPhase.Create,
                Sequence = input.Sequence,
                OperationGeneration = input.Generation,
                Chunk = input.Chunk.Clone(),
                StreamingElementId = input.StreamingElementId,
            },
        };
    }

    public ReplyOperationStepEvent CreateStreamStep(LarkCardStreamOperationStepInput input)
    {
        var operationId = LarkCardReplyOperationIds.BuildOperationId(
            input.CorrelationId,
            LarkCardOperationPhase.Stream,
            input.Sequence,
            input.Generation);
        return new ReplyOperationStepEvent
        {
            OperationId = operationId,
            OperationName = "lark-card-stream",
            CorrelationId = input.CorrelationId,
            LeaseEpoch = input.Generation,
            LarkCard = new LarkCardOperationStepPayload
            {
                Operation = LarkCardOperationPhase.Stream,
                Sequence = input.Sequence,
                OperationGeneration = input.Generation,
                Chunk = input.Chunk.Clone(),
                CardId = input.CardId,
                StreamingElementId = input.StreamingElementId,
            },
        };
    }

    public ReplyOperationStepEvent CreateFinalizeStep(LarkCardFinalizeOperationStepInput input)
    {
        var operationId = LarkCardReplyOperationIds.BuildOperationId(
            input.CorrelationId,
            LarkCardOperationPhase.Finalize,
            input.Sequence,
            input.Generation);
        var step = new ReplyOperationStepEvent
        {
            OperationId = operationId,
            OperationName = "lark-card-finalize",
            CorrelationId = input.CorrelationId,
            LeaseEpoch = input.Generation,
            LarkCard = new LarkCardOperationStepPayload
            {
                Operation = LarkCardOperationPhase.Finalize,
                Sequence = input.Sequence,
                OperationGeneration = input.Generation,
                Activity = input.ActivityForToken.Clone(),
                CommandId = input.CommandId,
                FinalText = input.FinalText,
                LastFlushedText = input.LastFlushedText,
                CardId = input.CardId,
                CardMessageId = input.CardMessageId,
                StreamingElementId = input.StreamingElementId,
                FinalDiffers = input.FinalDiffers,
            },
        };
        step.LarkCard.AppendedHistory.AddRange(input.AppendedHistory.Select(entry => entry.Clone()));
        return step;
    }

    public async Task ExecuteAsync(
        IReplyOperationActorContext context,
        ReplyOperationStepEvent evt,
        CancellationToken ct)
    {
        var step = evt.LarkCard;
        var correlationId = evt.CorrelationId;
        if (!context.MatchesLarkCardInFlight(
                correlationId,
                step.Operation,
                step.Sequence,
                step.OperationGeneration,
                NormalizeOptional(step.CardId)))
        {
            return;
        }

        var runtimeContext = step.Operation == LarkCardOperationPhase.Finalize
            ? context.BuildNyxRelayRuntimeContext(correlationId, step.Activity, string.Empty, 0)
            : context.BuildNyxRelayRuntimeContext(
                step.Chunk?.CorrelationId,
                step.Chunk?.Activity,
                step.Chunk?.ReplyToken,
                step.Chunk?.ReplyTokenExpiresAtUnixMs ?? 0);

        switch (step.Operation)
        {
            case LarkCardOperationPhase.Create:
                await ExecuteCreateAsync(context, step, correlationId, runtimeContext, ct)
                    .ConfigureAwait(false);
                return;
            case LarkCardOperationPhase.Stream:
                await ExecuteStreamAsync(context, step, correlationId, runtimeContext, ct)
                    .ConfigureAwait(false);
                return;
            case LarkCardOperationPhase.Finalize:
                context.RestoreRuntimeTransportCredentials(step.Activity, runtimeContext);
                await ExecuteFinalizeAsync(context, step, correlationId, runtimeContext, ct)
                    .ConfigureAwait(false);
                return;
        }
    }

    private async Task ExecuteCreateAsync(
        IReplyOperationActorContext context,
        LarkCardOperationStepPayload step,
        string correlationId,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        var chunk = step.Chunk?.Clone() ?? new LlmReplyCardStreamChunkEvent();
        LarkCardOperationCompletedEvent signal;
        try
        {
            var result = await runner.RunCardCreateAsync(
                    chunk,
                    step.StreamingElementId,
                    runtimeContext,
                    ct)
                .ConfigureAwait(false);
            signal = new LarkCardOperationCompletedEvent
            {
                OperationId = LarkCardReplyOperationIds.BuildOperationId(
                    correlationId,
                    LarkCardOperationPhase.Create,
                    step.Sequence,
                    step.OperationGeneration),
                CorrelationId = correlationId,
                Operation = LarkCardOperationPhase.Create,
                Sequence = step.Sequence,
                OperationGeneration = step.OperationGeneration,
                State = result.Success
                    ? LarkCardOperationResultState.Succeeded
                    : LarkCardOperationResultState.Failed,
                RawResult = ToRawResult(result),
                Chunk = chunk,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Card create executor threw. correlation={CorrelationId}", correlationId);
            signal = new LarkCardOperationCompletedEvent
            {
                OperationId = LarkCardReplyOperationIds.BuildOperationId(
                    correlationId,
                    LarkCardOperationPhase.Create,
                    step.Sequence,
                    step.OperationGeneration),
                CorrelationId = correlationId,
                Operation = LarkCardOperationPhase.Create,
                Sequence = step.Sequence,
                OperationGeneration = step.OperationGeneration,
                State = LarkCardOperationResultState.Faulted,
                RawResult = ToRawFault(ex),
                Chunk = chunk,
            };
        }

        await context.DispatchReplyOperationCompletionAsync(
                signal,
                correlationId,
                "Lark card",
                ct)
            .ConfigureAwait(false);
    }

    private async Task ExecuteStreamAsync(
        IReplyOperationActorContext context,
        LarkCardOperationStepPayload step,
        string correlationId,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        var chunk = step.Chunk?.Clone() ?? new LlmReplyCardStreamChunkEvent();
        LarkCardOperationCompletedEvent signal;
        try
        {
            var result = await runner.RunCardStreamAsync(
                    chunk,
                    step.CardId,
                    step.StreamingElementId,
                    step.Sequence,
                    runtimeContext,
                    ct)
                .ConfigureAwait(false);
            signal = new LarkCardOperationCompletedEvent
            {
                OperationId = LarkCardReplyOperationIds.BuildOperationId(
                    correlationId,
                    LarkCardOperationPhase.Stream,
                    step.Sequence,
                    step.OperationGeneration),
                CorrelationId = correlationId,
                Operation = LarkCardOperationPhase.Stream,
                Sequence = step.Sequence,
                OperationGeneration = step.OperationGeneration,
                State = result.Success
                    ? LarkCardOperationResultState.Succeeded
                    : LarkCardOperationResultState.Failed,
                RawResult = ToRawResult(result),
                CardId = step.CardId,
                StreamingElementId = step.StreamingElementId,
                Chunk = chunk,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Card stream executor threw. correlation={CorrelationId}, seq={Sequence}",
                correlationId,
                step.Sequence);
            signal = new LarkCardOperationCompletedEvent
            {
                OperationId = LarkCardReplyOperationIds.BuildOperationId(
                    correlationId,
                    LarkCardOperationPhase.Stream,
                    step.Sequence,
                    step.OperationGeneration),
                CorrelationId = correlationId,
                Operation = LarkCardOperationPhase.Stream,
                Sequence = step.Sequence,
                OperationGeneration = step.OperationGeneration,
                State = LarkCardOperationResultState.Faulted,
                RawResult = ToRawFault(ex),
                CardId = step.CardId,
                StreamingElementId = step.StreamingElementId,
                Chunk = chunk,
            };
        }

        await context.DispatchReplyOperationCompletionAsync(
                signal,
                correlationId,
                "Lark card",
                ct)
            .ConfigureAwait(false);
    }

    private async Task ExecuteFinalizeAsync(
        IReplyOperationActorContext context,
        LarkCardOperationStepPayload step,
        string correlationId,
        ConversationTurnRuntimeContext runtimeContext,
        CancellationToken ct)
    {
        var activityForToken = step.Activity?.Clone() ?? new ChatActivity();
        LarkCardOperationCompletedEvent signal;
        try
        {
            var result = await runner.RunCardFinalizeAsync(
                    activityForToken,
                    step.CardId,
                    step.StreamingElementId,
                    step.FinalText,
                    step.FinalDiffers,
                    step.Sequence,
                    runtimeContext,
                    ct)
                .ConfigureAwait(false);
            signal = new LarkCardOperationCompletedEvent
            {
                OperationId = LarkCardReplyOperationIds.BuildOperationId(
                    correlationId,
                    LarkCardOperationPhase.Finalize,
                    step.Sequence,
                    step.OperationGeneration),
                CorrelationId = correlationId,
                Operation = LarkCardOperationPhase.Finalize,
                Sequence = step.Sequence,
                OperationGeneration = step.OperationGeneration,
                State = result.Success
                    ? LarkCardOperationResultState.Succeeded
                    : LarkCardOperationResultState.Failed,
                RawResult = ToRawResult(result),
                CardId = step.CardId,
                CardMessageId = step.CardMessageId,
                CommandId = step.CommandId,
                Activity = CloneForDurableState(activityForToken) ?? new ChatActivity(),
                FinalText = step.FinalText,
                LastFlushedText = step.LastFlushedText,
            };
            signal.AppendedHistory.AddRange(step.AppendedHistory.Select(entry => entry.Clone()));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Card finalize executor threw. correlation={CorrelationId}", correlationId);
            signal = new LarkCardOperationCompletedEvent
            {
                OperationId = LarkCardReplyOperationIds.BuildOperationId(
                    correlationId,
                    LarkCardOperationPhase.Finalize,
                    step.Sequence,
                    step.OperationGeneration),
                CorrelationId = correlationId,
                Operation = LarkCardOperationPhase.Finalize,
                Sequence = step.Sequence,
                OperationGeneration = step.OperationGeneration,
                State = LarkCardOperationResultState.Faulted,
                RawResult = ToRawFault(ex),
                CardId = step.CardId,
                CardMessageId = step.CardMessageId,
                CommandId = step.CommandId,
                Activity = CloneForDurableState(activityForToken) ?? new ChatActivity(),
                FinalText = step.FinalText,
                LastFlushedText = step.LastFlushedText,
            };
            signal.AppendedHistory.AddRange(step.AppendedHistory.Select(entry => entry.Clone()));
        }

        await context.DispatchReplyOperationCompletionAsync(
                signal,
                correlationId,
                "Lark card",
                ct)
            .ConfigureAwait(false);
    }

    private static LarkCardOperationRawResult ToRawResult(ConversationCardCreateResult result) =>
        new()
        {
            CardId = result.CardId ?? string.Empty,
            CardMessageId = result.CardMessageId ?? string.Empty,
            IsRateLimited = result.IsRateLimited,
            IsTableLimitExceeded = result.IsTableLimitExceeded,
            IsCardUnavailable = result.IsCardUnavailable,
            IsPostSendFailure = result.IsPostSendFailure,
            RawErrorCode = result.ErrorCode ?? string.Empty,
            RawErrorSummary = result.ErrorSummary ?? string.Empty,
        };

    private static LarkCardOperationRawResult ToRawResult(ConversationCardStreamResult result) =>
        new()
        {
            IsRateLimited = result.IsRateLimited,
            IsTableLimitExceeded = result.IsTableLimitExceeded,
            IsCardUnavailable = result.IsCardUnavailable,
            RawErrorCode = result.ErrorCode ?? string.Empty,
            RawErrorSummary = result.ErrorSummary ?? string.Empty,
        };

    private static LarkCardOperationRawResult ToRawResult(ConversationCardFinalizeResult result) =>
        new()
        {
            FinalTextWritten = result.FinalTextWritten,
            RawErrorCode = result.ErrorCode ?? string.Empty,
            RawErrorSummary = result.ErrorSummary ?? string.Empty,
        };

    private static LarkCardOperationRawResult ToRawFault(Exception ex) =>
        new()
        {
            ExceptionType = ex.GetType().Name,
            ExceptionMessage = ex.Message,
        };

    private static ChatActivity? CloneForDurableState(ChatActivity? activity)
    {
        if (activity is null)
            return null;

        var clone = activity.Clone();
        if (clone.TransportExtras is not null)
            clone.TransportExtras.NyxUserAccessToken = string.Empty;
        return clone;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

internal static class LarkCardReplyOperationIds
{
    public static string BuildOperationId(
        string correlationId,
        LarkCardOperationPhase operation,
        long sequence,
        long generation) =>
        $"{correlationId}:{operation}:{sequence}:{generation}";
}
