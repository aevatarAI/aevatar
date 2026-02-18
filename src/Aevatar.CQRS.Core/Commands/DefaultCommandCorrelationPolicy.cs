using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

public sealed class DefaultCommandCorrelationPolicy : ICommandCorrelationPolicy
{
    public CommandCorrelation CreateNew(string actorId, string? sessionId = null)
    {
        ValidateActorId(actorId);
        return new CommandCorrelation(
            ExecutionId: Guid.NewGuid().ToString("N"),
            SessionId: ResolveSessionId(sessionId),
            ActorId: actorId,
            CorrelationId: Guid.NewGuid().ToString("N"));
    }

    public CommandCorrelation CreateForExecution(string actorId, string executionId, string? sessionId = null)
    {
        ValidateActorId(actorId);
        if (string.IsNullOrWhiteSpace(executionId))
            throw new ArgumentException("Execution id is required.", nameof(executionId));

        return new CommandCorrelation(
            ExecutionId: executionId,
            SessionId: ResolveSessionId(sessionId),
            ActorId: actorId,
            CorrelationId: Guid.NewGuid().ToString("N"));
    }

    public bool TryResolve(EventEnvelope envelope, out CommandCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var executionId = envelope.Metadata.TryGetValue(CommandCorrelationMetadataKeys.ExecutionId, out var executionValue)
            ? executionValue
            : null;

        if (string.IsNullOrWhiteSpace(executionId))
        {
            correlation = null!;
            return false;
        }

        var actorId = envelope.Metadata.TryGetValue(CommandCorrelationMetadataKeys.ActorId, out var actorValue)
            ? actorValue
            : envelope.TargetActorId;

        if (string.IsNullOrWhiteSpace(actorId))
            actorId = envelope.PublisherId;

        if (string.IsNullOrWhiteSpace(actorId))
        {
            correlation = null!;
            return false;
        }

        var sessionId = envelope.Metadata.TryGetValue(CommandCorrelationMetadataKeys.SessionId, out var sessionValue)
            ? sessionValue
            : $"session-{executionId}";

        var correlationId = envelope.Metadata.TryGetValue(CommandCorrelationMetadataKeys.CorrelationId, out var correlationValue)
            ? correlationValue
            : envelope.CorrelationId;

        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString("N");

        correlation = new CommandCorrelation(executionId!, sessionId, actorId, correlationId);
        return true;
    }

    private static void ValidateActorId(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor id is required.", nameof(actorId));
    }

    private static string ResolveSessionId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId)
            ? $"session-{Guid.NewGuid():N}"
            : sessionId;
}
