namespace Aevatar.CQRS.Projection.Core.Abstractions;

public interface IProjectionScopeIntrospectionQueryPort
{
    Task<ProjectionScopeIntrospectionSnapshot?> GetAsync(
        string scopeActorId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ProjectionObservedEnvelopeSnapshot>> ListRecentEnvelopesAsync(
        string scopeActorId,
        int take,
        CancellationToken ct = default);
}

public sealed record ProjectionScopeIntrospectionSnapshot(
    string ScopeActorId,
    string RootActorId,
    string ProjectionKind,
    string SessionId,
    ProjectionRuntimeMode Mode,
    bool Active,
    bool ObservationAttached,
    bool Released,
    long StateVersion,
    long LastObservedVersion,
    long LastSuccessfulVersion,
    int FailureCount,
    DateTimeOffset UpdatedAt);

public sealed record ProjectionObservedEnvelopeSnapshot(
    string EventId,
    string TypeUrl,
    long StateVersion,
    DateTimeOffset? TimestampUtc);
