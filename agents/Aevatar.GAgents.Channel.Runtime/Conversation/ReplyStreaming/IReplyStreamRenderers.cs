using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgents.Channel.Runtime;

internal interface IReplyOperationStepRenderer
{
    bool CanHandle(ReplyOperationStepEvent evt);

    Task ExecuteAsync(
        IReplyOperationActorContext context,
        ReplyOperationStepEvent evt,
        CancellationToken ct);
}

internal interface INyxRelayTextReplyStreamRenderer : IReplyOperationStepRenderer
{
    ReplyOperationStepEvent CreateStep(NyxRelayTextOperationStepInput input);
}

internal interface ILarkCardReplyStreamRenderer : IReplyOperationStepRenderer
{
    ReplyOperationStepEvent CreateCreateStep(LarkCardCreateOperationStepInput input);

    ReplyOperationStepEvent CreateStreamStep(LarkCardStreamOperationStepInput input);

    ReplyOperationStepEvent CreateFinalizeStep(LarkCardFinalizeOperationStepInput input);
}

internal interface IReplyOperationActorContext
{
    bool MatchesNyxRelayTextInFlight(
        string correlationId,
        NyxRelayTextOperationKind operation,
        long sequence,
        long generation);

    bool MatchesLarkCardInFlight(
        string correlationId,
        LarkCardOperationPhase operation,
        long sequence,
        long generation,
        string? cardId);

    ConversationTurnRuntimeContext BuildNyxRelayRuntimeContext(
        string? correlationId,
        ChatActivity? activity,
        string? replyToken,
        long replyTokenExpiresAtUnixMs);

    void RestoreRuntimeTransportCredentials(
        ChatActivity? activity,
        ConversationTurnRuntimeContext runtimeContext);

    Task DispatchReplyOperationCompletionAsync(
        IMessage evt,
        string correlationId,
        string operationName,
        CancellationToken ct);
}

internal sealed record NyxRelayTextOperationStepInput(
    NyxRelayTextOperationKind Operation,
    LlmReplyStreamChunkEvent Chunk,
    string CorrelationId,
    string? CurrentPlatformMessageId,
    string? CommandId,
    string? FinalText,
    string? LastFlushedText,
    int EditCount,
    long Sequence,
    long Generation);

internal sealed record LarkCardCreateOperationStepInput(
    LlmReplyCardStreamChunkEvent Chunk,
    string CorrelationId,
    string StreamingElementId,
    long Sequence,
    long Generation);

internal sealed record LarkCardStreamOperationStepInput(
    LlmReplyCardStreamChunkEvent Chunk,
    string CorrelationId,
    string CardId,
    string StreamingElementId,
    long Sequence,
    long Generation);

internal sealed record LarkCardFinalizeOperationStepInput(
    ChatActivity ActivityForToken,
    string CorrelationId,
    string CommandId,
    string FinalText,
    string LastFlushedText,
    string CardId,
    string CardMessageId,
    string StreamingElementId,
    bool FinalDiffers,
    IReadOnlyList<ConversationHistoryEntry> AppendedHistory,
    long Sequence,
    long Generation);
