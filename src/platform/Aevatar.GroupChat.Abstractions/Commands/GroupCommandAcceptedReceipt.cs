namespace Aevatar.GroupChat.Abstractions.Commands;

public sealed record GroupCommandAcceptedReceipt(
    string TargetActorId,
    string CommandId,
    string CorrelationId);
