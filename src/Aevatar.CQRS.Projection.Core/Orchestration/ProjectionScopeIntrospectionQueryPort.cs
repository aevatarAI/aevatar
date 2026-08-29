using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionScopeIntrospectionQueryPort : IProjectionScopeIntrospectionQueryPort
{
    private const int DefaultTake = 20;
    private const int MaxTake = 50;

    private readonly IProjectionDocumentReader<ProjectionScopeStatusDocument, string> _documentReader;

    public ProjectionScopeIntrospectionQueryPort(
        IProjectionDocumentReader<ProjectionScopeStatusDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<ProjectionScopeIntrospectionSnapshot?> GetAsync(
        string scopeActorId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeActorId);
        var document = await _documentReader.GetAsync(scopeActorId, ct);
        return document == null ? null : Map(document);
    }

    public async Task<IReadOnlyList<ProjectionObservedEnvelopeSnapshot>> ListRecentEnvelopesAsync(
        string scopeActorId,
        int take,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeActorId);
        var document = await _documentReader.GetAsync(scopeActorId, ct);
        if (document == null)
            return [];

        var boundedTake = Math.Clamp(take <= 0 ? DefaultTake : take, 1, MaxTake);
        return document.RecentObservedEnvelopes
            .Reverse()
            .Take(boundedTake)
            .Select(item => new ProjectionObservedEnvelopeSnapshot(
                item.EventId,
                item.TypeUrl,
                item.StateVersion,
                item.TimestampUtc?.ToDateTimeOffset()))
            .ToList();
    }

    private static ProjectionScopeIntrospectionSnapshot Map(ProjectionScopeStatusDocument document)
    {
        var snapshot = new ProjectionScopeIntrospectionSnapshot(
            document.ScopeActorId,
            document.RootActorId,
            document.ProjectionKind,
            document.SessionId,
            ProjectionScopeModeMapper.ToRuntime(document.Mode),
            document.Active,
            document.ObservationAttached,
            document.Released,
            document.StateVersion,
            document.ReceivedEnvelopeTotal,
            document.AttemptedEnvelopeTotal,
            document.SuccessfulMaterializationTotal,
            document.FailedAttemptTotal,
            document.RetryExhaustedTotal,
            document.RetryExhaustedFailureCount,
            document.UnresolvedFailureCount,
            document.OldestUnresolvedFailureAtUtc?.ToDateTimeOffset(),
            document.FailureDiagnosticDroppedTotal,
            document.SourceVersions.Select(source => new ProjectionSourceVersionSnapshot(
                source.SourceActorId,
                source.HighestSeenVersion,
                source.LastSuccessfulVersion,
                source.VersionGap)).ToList(),
            document.UpdatedAt);
        return snapshot with
        {
            InFlightSource = document.InFlightSource == null
                ? null
                : new ProjectionInFlightSourceSnapshot(
                    document.InFlightSource.ActorId,
                    document.InFlightSource.StateVersion,
                    document.InFlightSource.EventId),
        };
    }
}
