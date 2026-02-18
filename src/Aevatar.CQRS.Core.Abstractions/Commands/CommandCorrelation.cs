namespace Aevatar.CQRS.Core.Abstractions.Commands;

public sealed record CommandCorrelation(
    string ExecutionId,
    string SessionId,
    string ActorId,
    string CorrelationId);
