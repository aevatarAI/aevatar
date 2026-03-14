using Aevatar.Demos.CaseProjection.Reducers;
using Aevatar.Demos.CaseProjection.Stores;

namespace Aevatar.Demos.CaseProjection.Projectors;

public sealed class CaseReadModelProjector
    : IProjectionProjector<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>>
{
    private readonly IProjectionDocumentStore<CaseProjectionReadModel, string> _store;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IProjectionEventReducer<CaseProjectionReadModel, CaseProjectionContext>>> _reducersByType;

    public CaseReadModelProjector(
        IProjectionDocumentStore<CaseProjectionReadModel, string> store,
        IEnumerable<IProjectionEventReducer<CaseProjectionReadModel, CaseProjectionContext>> reducers)
    {
        _store = store;
        _reducersByType = reducers
            .GroupBy(x => x.EventTypeUrl, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<IProjectionEventReducer<CaseProjectionReadModel, CaseProjectionContext>>)x.ToList(),
                StringComparer.Ordinal);
    }

    public ValueTask InitializeAsync(CaseProjectionContext context, CancellationToken ct = default)
    {
        var model = new CaseProjectionReadModel
        {
            RunId = context.RunId,
            RootActorId = context.RootActorId,
            CaseId = context.CaseId,
            CaseType = context.CaseType,
            Input = context.Input,
            StartedAt = context.StartedAt,
            EndedAt = context.StartedAt,
            Status = "pending",
            Summary = new CaseProjectionSummary(),
        };

        return new ValueTask(_store.UpsertAsync(model, ct));
    }

    public ValueTask ProjectAsync(CaseProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        var typeUrl = envelope.Payload?.TypeUrl;
        if (string.IsNullOrWhiteSpace(typeUrl)) return ValueTask.CompletedTask;
        if (!_reducersByType.TryGetValue(typeUrl, out var reducers)) return ValueTask.CompletedTask;
        if (!string.IsNullOrWhiteSpace(envelope.Id) && !context.TryMarkProcessed(envelope.Id))
            return ValueTask.CompletedTask;

        var now = ResolveEventTimestamp(envelope);
        return new ValueTask(ProjectCoreAsync(context, envelope, reducers, now, ct));
    }

    public ValueTask CompleteAsync(
        CaseProjectionContext context,
        IReadOnlyList<CaseTopologyEdge> topology,
        CancellationToken ct = default)
    {
        return new ValueTask(CompleteCoreAsync(context, topology, ct));
    }

    private async Task ProjectCoreAsync(
        CaseProjectionContext context,
        EventEnvelope envelope,
        IReadOnlyList<IProjectionEventReducer<CaseProjectionReadModel, CaseProjectionContext>> reducers,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var report = await _store.GetAsync(context.RunId, ct);
        if (report == null)
            throw new CaseReadModelNotFoundException(context.RunId);

        var mutated = false;
        foreach (var reducer in reducers)
            mutated |= reducer.Reduce(report, context, envelope, now);

        if (!mutated)
            return;

        CaseProjectionMutations.RefreshDerivedFields(report);
        await _store.UpsertAsync(report, ct);
    }

    private async Task CompleteCoreAsync(
        CaseProjectionContext context,
        IReadOnlyList<CaseTopologyEdge> topology,
        CancellationToken ct)
    {
        var report = await _store.GetAsync(context.RunId, ct);
        if (report == null)
            throw new CaseReadModelNotFoundException(context.RunId);

        report.Topology = topology
            .Select(x => new CaseTopologyEdge(x.Parent, x.Child))
            .ToList();

        if (report.EndedAt < report.StartedAt)
            report.EndedAt = DateTimeOffset.UtcNow;

        CaseProjectionMutations.RefreshDerivedFields(report);
        await _store.UpsertAsync(report, ct);
    }

    private static DateTimeOffset ResolveEventTimestamp(EventEnvelope envelope)
    {
        var ts = envelope.Timestamp;
        if (ts == null)
            return DateTimeOffset.UtcNow;

        var dt = ts.ToDateTime();
        if (dt.Kind != DateTimeKind.Utc)
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(dt);
    }
}
