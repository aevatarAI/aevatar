using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Runtime;

internal sealed class NyxRelayTextReplyStreamRenderer(
    IConversationTurnRunner runner,
    ILogger<NyxRelayTextReplyStreamRenderer> logger) : INyxRelayTextReplyStreamRenderer
{
    public bool CanHandle(ReplyOperationStepEvent evt) =>
        evt.PayloadCase == ReplyOperationStepEvent.PayloadOneofCase.NyxRelayText;

    public ReplyOperationStepEvent CreateStep(NyxRelayTextOperationStepInput input)
    {
        var operationId = NyxRelayTextReplyOperationIds.BuildOperationId(
            input.CorrelationId,
            input.Operation,
            input.Sequence,
            input.Generation);
        return new ReplyOperationStepEvent
        {
            OperationId = operationId,
            OperationName = $"nyx-relay-text-{input.Operation}",
            CorrelationId = input.CorrelationId,
            LeaseEpoch = input.Generation,
            NyxRelayText = new NyxRelayTextOperationStepPayload
            {
                Operation = input.Operation,
                Sequence = input.Sequence,
                OperationGeneration = input.Generation,
                Chunk = input.Chunk.Clone(),
                CurrentPlatformMessageId = input.CurrentPlatformMessageId ?? string.Empty,
                CommandId = input.CommandId ?? string.Empty,
                FinalText = input.FinalText ?? string.Empty,
                LastFlushedText = input.LastFlushedText ?? string.Empty,
                EditCount = input.EditCount,
            },
        };
    }

    public async Task ExecuteAsync(
        IReplyOperationActorContext context,
        ReplyOperationStepEvent evt,
        CancellationToken ct)
    {
        var step = evt.NyxRelayText;
        var correlationId = evt.CorrelationId;
        if (!context.MatchesNyxRelayTextInFlight(
                correlationId,
                step.Operation,
                step.Sequence,
                step.OperationGeneration))
        {
            return;
        }

        var chunk = step.Chunk?.Clone() ?? new LlmReplyStreamChunkEvent();
        var runtimeContext = context.BuildNyxRelayRuntimeContext(
            chunk.CorrelationId,
            chunk.Activity,
            chunk.ReplyToken,
            chunk.ReplyTokenExpiresAtUnixMs);

        NyxRelayTextOperationCompletedEvent signal;
        try
        {
            var result = await runner.RunStreamChunkAsync(
                    chunk,
                    NormalizeOptional(step.CurrentPlatformMessageId),
                    step.Operation,
                    runtimeContext,
                    ct)
                .ConfigureAwait(false);
            signal = new NyxRelayTextOperationCompletedEvent
            {
                OperationId = NyxRelayTextReplyOperationIds.BuildOperationId(
                    correlationId,
                    step.Operation,
                    step.Sequence,
                    step.OperationGeneration),
                CorrelationId = correlationId,
                Operation = step.Operation,
                Sequence = step.Sequence,
                OperationGeneration = step.OperationGeneration,
                State = result.Success
                    ? NyxRelayTextOperationResultState.Succeeded
                    : NyxRelayTextOperationResultState.Failed,
                RawResult = ToRawResult(result),
                Chunk = chunk,
                CurrentPlatformMessageId = step.CurrentPlatformMessageId ?? string.Empty,
                CommandId = step.CommandId ?? string.Empty,
                FinalText = step.FinalText ?? string.Empty,
                LastFlushedText = step.LastFlushedText ?? string.Empty,
                EditCount = step.EditCount,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Nyx relay text operation executor threw. correlation={CorrelationId}, operation={Operation}",
                correlationId,
                step.Operation);
            signal = new NyxRelayTextOperationCompletedEvent
            {
                OperationId = NyxRelayTextReplyOperationIds.BuildOperationId(
                    correlationId,
                    step.Operation,
                    step.Sequence,
                    step.OperationGeneration),
                CorrelationId = correlationId,
                Operation = step.Operation,
                Sequence = step.Sequence,
                OperationGeneration = step.OperationGeneration,
                State = NyxRelayTextOperationResultState.Faulted,
                RawResult = ToRawFault(ex),
                Chunk = chunk,
                CurrentPlatformMessageId = step.CurrentPlatformMessageId ?? string.Empty,
                CommandId = step.CommandId ?? string.Empty,
                FinalText = step.FinalText ?? string.Empty,
                LastFlushedText = step.LastFlushedText ?? string.Empty,
                EditCount = step.EditCount,
            };
        }

        await context.DispatchReplyOperationCompletionAsync(
                signal,
                correlationId,
                "Nyx relay text",
                ct)
            .ConfigureAwait(false);
    }

    private static NyxRelayTextOperationRawResult ToRawResult(ConversationStreamChunkResult result) =>
        new()
        {
            PlatformMessageId = result.PlatformMessageId ?? string.Empty,
            EditUnsupported = result.EditUnsupported,
            RawErrorCode = result.ErrorCode ?? string.Empty,
            RawErrorSummary = result.ErrorSummary ?? string.Empty,
            FailureKind = result.FailureKind,
            RetryAfterMs = result.RetryAfter.HasValue ? (long)result.RetryAfter.Value.TotalMilliseconds : 0,
            HttpStatus = result.HttpStatus,
            RawErrorKey = result.RawErrorKey ?? string.Empty,
            RawErrorCodeValue = result.RawErrorCode,
        };

    private static NyxRelayTextOperationRawResult ToRawFault(Exception ex) =>
        new()
        {
            ExceptionType = ex.GetType().Name,
            ExceptionMessage = ex.Message,
        };

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

internal static class NyxRelayTextReplyOperationIds
{
    public static string BuildOperationId(
        string correlationId,
        NyxRelayTextOperationKind operation,
        long sequence,
        long generation) =>
        $"{correlationId}:{operation}:{sequence}:{generation}";
}
