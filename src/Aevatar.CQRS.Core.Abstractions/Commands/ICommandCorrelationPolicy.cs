namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandCorrelationPolicy
{
    CommandCorrelation CreateNew(string actorId, string? sessionId = null);

    CommandCorrelation CreateForExecution(string actorId, string executionId, string? sessionId = null);

    bool TryResolve(EventEnvelope envelope, out CommandCorrelation correlation);
}
