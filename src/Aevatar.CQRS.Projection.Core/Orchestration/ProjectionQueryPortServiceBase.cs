namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Generic query port base that centralizes query enable-gate behavior.
/// </summary>
public abstract class ProjectionQueryPortServiceBase<TSnapshot, TTimelineItem, TRelationItem, TRelationSubgraph>
{
    private readonly Func<bool> _queryEnabledAccessor;

    protected ProjectionQueryPortServiceBase(Func<bool> queryEnabledAccessor)
    {
        _queryEnabledAccessor = queryEnabledAccessor ?? throw new ArgumentNullException(nameof(queryEnabledAccessor));
    }

    protected bool QueryEnabledCore => _queryEnabledAccessor();

    protected async Task<TSnapshot?> GetSnapshotAsync(
        string entityId,
        CancellationToken ct = default)
    {
        if (!QueryEnabledCore || string.IsNullOrWhiteSpace(entityId))
            return default;

        return await ReadSnapshotCoreAsync(entityId, ct);
    }

    protected async Task<IReadOnlyList<TSnapshot>> ListSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        if (!QueryEnabledCore)
            return [];

        return await ReadSnapshotsCoreAsync(take, ct);
    }

    protected async Task<IReadOnlyList<TTimelineItem>> ListTimelineAsync(
        string entityId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!QueryEnabledCore || string.IsNullOrWhiteSpace(entityId))
            return [];

        return await ReadTimelineCoreAsync(entityId, take, ct);
    }

    protected async Task<IReadOnlyList<TRelationItem>> GetRelationsAsync(
        string entityId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!QueryEnabledCore || string.IsNullOrWhiteSpace(entityId))
            return [];

        return await ReadRelationsCoreAsync(entityId, take, ct);
    }

    protected async Task<TRelationSubgraph> GetRelationSubgraphAsync(
        string entityId,
        int depth = 2,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!QueryEnabledCore || string.IsNullOrWhiteSpace(entityId))
            return CreateEmptyRelationSubgraph(entityId);

        return await ReadRelationSubgraphCoreAsync(entityId, depth, take, ct);
    }

    protected abstract Task<TSnapshot?> ReadSnapshotCoreAsync(
        string entityId,
        CancellationToken ct);

    protected abstract Task<IReadOnlyList<TSnapshot>> ReadSnapshotsCoreAsync(
        int take,
        CancellationToken ct);

    protected abstract Task<IReadOnlyList<TTimelineItem>> ReadTimelineCoreAsync(
        string entityId,
        int take,
        CancellationToken ct);

    protected abstract Task<IReadOnlyList<TRelationItem>> ReadRelationsCoreAsync(
        string entityId,
        int take,
        CancellationToken ct);

    protected abstract Task<TRelationSubgraph> ReadRelationSubgraphCoreAsync(
        string entityId,
        int depth,
        int take,
        CancellationToken ct);

    protected virtual TRelationSubgraph CreateEmptyRelationSubgraph(string entityId) => default!;
}
