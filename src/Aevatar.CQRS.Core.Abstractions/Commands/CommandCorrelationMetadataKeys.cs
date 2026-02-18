namespace Aevatar.CQRS.Core.Abstractions.Commands;

public static class CommandCorrelationMetadataKeys
{
    public const string ExecutionId = "execution_id";
    public const string SessionId = "session_id";
    public const string ActorId = "actor_id";
    public const string CorrelationId = "correlation_id";
}
