namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IGAgentActorRegistryCommandPort
{
    Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
        GAgentActorRegistration registration,
        CancellationToken cancellationToken = default);

    Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
        GAgentActorRegistration registration,
        CancellationToken cancellationToken = default);
}

public interface IGAgentActorRegistryQueryPort
{
    Task<GAgentActorRegistrySnapshot> ListActorsAsync(
        string scopeId,
        CancellationToken cancellationToken = default);
}

public interface IScopeResourceAdmissionPort
{
    Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
        ScopeResourceTarget target,
        CancellationToken cancellationToken = default);
}

public sealed record GAgentActorRegistration(
    string ScopeId,
    string GAgentType,
    string ActorId);

public sealed record GAgentActorRegistryCommandReceipt(
    GAgentActorRegistration Registration,
    GAgentActorRegistryCommandStage Stage)
{
    public bool IsAdmissionVisible => Stage == GAgentActorRegistryCommandStage.AdmissionVisible;
}

public enum GAgentActorRegistryCommandStage
{
    AcceptedForDispatch = 0,
    AdmissionVisible = 1,
}

public sealed record GAgentActorRegistrySnapshot(
    string ScopeId,
    IReadOnlyList<GAgentActorGroup> Groups,
    long StateVersion,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ObservedAt);

public sealed record GAgentActorGroup(string GAgentType, IReadOnlyList<string> ActorIds);

public sealed record ScopeResourceTarget(
    string ScopeId,
    ScopeResourceKind ResourceKind,
    string GAgentType,
    string ActorId,
    ScopeResourceOperation Operation);

public enum ScopeResourceKind
{
    GAgentActor = 0,
}

public enum ScopeResourceOperation
{
    Use = 0,
    Delete = 1,
    Chat = 2,
    Stream = 3,
    Approve = 4,
    Join = 5,
    ListParticipants = 6,
    DraftRunReuse = 7,
}

public sealed record ScopeResourceAdmissionResult(
    ScopeResourceAdmissionStatus Status)
{
    public bool IsAllowed => Status == ScopeResourceAdmissionStatus.Allowed;

    public static ScopeResourceAdmissionResult Allowed() =>
        new(ScopeResourceAdmissionStatus.Allowed);

    public static ScopeResourceAdmissionResult Denied() =>
        new(ScopeResourceAdmissionStatus.Denied);

    public static ScopeResourceAdmissionResult NotFound() =>
        new(ScopeResourceAdmissionStatus.NotFound);

    public static ScopeResourceAdmissionResult ScopeMismatch() =>
        new(ScopeResourceAdmissionStatus.ScopeMismatch);

    public static ScopeResourceAdmissionResult Unavailable() =>
        new(ScopeResourceAdmissionStatus.Unavailable);
}

public enum ScopeResourceAdmissionStatus
{
    Allowed = 0,
    Denied = 1,
    NotFound = 2,
    ScopeMismatch = 3,
    Unavailable = 4,
}
