namespace Aevatar.GAgentService.Abstractions.ScopeGAgents;

public enum GAgentRunTerminalStatus
{
    Unknown = 0,
    TextMessageCompleted = 1,
    RunFinished = 2,
    Failed = 3,
    OutcomeUncertain = 4,
}

public static class GAgentRunFailureCodes
{
    public const string CapacityExhausted = "CAPACITY_EXHAUSTED";
    public const string OutcomeUncertain = "SESSION_OUTCOME_UNCERTAIN";
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

public interface IGAgentRunTerminalProjectionLease
{
    string ActorId { get; }

    string CorrelationId { get; }

    GAgentRunTerminalInteractionKind InteractionKind { get; }
}

public interface IGAgentRunTerminalProjectionPort
{
    // Refactor (iter52/issue-905-public-projection-ensure-ports):
    //   Old pattern: Public application/agent projection ports exposed actorId-based EnsureProjection/EnsureActorProjection as general callable surface.
    //   New principle: Projection activation is owned by projection bootstrap/lease/session contracts (bootstrap-internal); public application/query ports only support Attach*/Release*/Query* on existing leases.
    // Refactor (iter37/cluster-037-gagentservice-binders-attach-existing):
    //   Old pattern: GAgentService interaction binders synchronously prime projection sessions before dispatch(request-path projection activation in BindAsync).
    //   New principle: Attach-only to existing projection sessions/materialization leases via capability-specific attach-existing ports.
    //   Cold sessions return ProjectionUnavailable / pending before dispatch; no top-level live-observation exception.
    Task<IGAgentRunTerminalProjectionLease?> AttachExistingProjectionAsync(
        string actorId,
        string correlationId,
        GAgentRunTerminalInteractionKind interactionKind,
        CancellationToken ct = default);

    Task ReleaseProjectionAsync(
        IGAgentRunTerminalProjectionLease lease,
        CancellationToken ct = default);
}
