namespace Aevatar.GAgentService.Abstractions.Commands;

public sealed record ServiceCommandAcceptedReceipt(
    string TargetActorId,
    string CommandId,
    string CorrelationId);
