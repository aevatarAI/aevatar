namespace Aevatar.GAgentService.Abstractions.ScopeGAgents;

public enum GAgentRunTerminalStatus
{
    Unknown = 0,
    TextMessageCompleted = 1,
    RunFinished = 2,
    Failed = 3,
}

public enum GAgentRunTerminalInteractionKind
{
    Unknown = 0,
    DraftRun = 1,
    Approval = 2,
}

public sealed record GAgentRunTerminalSnapshot(
    string ActorId,
    string SessionId,
    string CorrelationId,
    GAgentRunTerminalInteractionKind InteractionKind,
    GAgentRunTerminalStatus Status,
    string ReasonCode,
    string ReasonMessage,
    long StateVersion,
    string LastEventId,
    DateTimeOffset ObservedAt);

public interface IGAgentRunTerminalQueryPort
{
    Task<GAgentRunTerminalSnapshot?> GetByCorrelationIdAsync(
        string actorId,
        string correlationId,
        CancellationToken ct = default);

    Task<GAgentRunTerminalSnapshot?> GetBySessionIdAsync(
        string actorId,
        string sessionId,
        CancellationToken ct = default);
}

public interface IGAgentRunTerminalProjectionPort
{
    Task EnsureProjectionAsync(
        string actorId,
        string correlationId,
        CancellationToken ct = default);
}
