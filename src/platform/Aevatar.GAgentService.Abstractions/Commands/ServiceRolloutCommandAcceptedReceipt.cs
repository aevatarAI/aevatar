namespace Aevatar.GAgentService.Abstractions.Commands;

public sealed record ServiceRolloutCommandAcceptedReceipt(
    string TargetActorId,
    string CommandId,
    string CorrelationId,
    bool WasNoOp,
    string Status);
