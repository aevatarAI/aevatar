using Aevatar.Demos.CaseProjections.Reducers;

namespace Aevatar.Demos.CaseProjections.Projectors;

public sealed class CaseReadModelProjector
    : IProjectionProjector<CaseProjectionContext, IReadOnlyList<CaseTopologyEdge>>
{
    private readonly IProjectionReadModelStore<CaseProjectionReadModel, string> _store;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IProjectionEventReducer<CaseProjectionReadModel, CaseProjectionContext>>> _reducersByType;

    public CaseReadModelProjector(
        IProjectionReadModelStore<CaseProjectionReadModel, string> store,
        IEnumerable<IProjectionEventReducer<CaseProjectionReadModel, CaseProjectionContext>> reducers)
    {
        _store = store;
        _reducersByType = reducers
            .OrderBy(x => x.Order)
            .GroupBy(x => x.EventTypeUrl, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<IProjectionEventReducer<CaseProjectionReadModel, CaseProjectionContext>>)x.ToList(),
                StringComparer.Ordinal);
    }

    public int Order => 0;

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
        return new ValueTask(_store.MutateAsync(context.RunId, report =>
        {
            foreach (var reducer in reducers)
                reducer.Reduce(report, context, envelope, now);

            CaseProjectionMutations.RefreshDerivedFields(report);
        }, ct));
    }

    public ValueTask CompleteAsync(
        CaseProjectionContext context,
        IReadOnlyList<CaseTopologyEdge> topology,
        CancellationToken ct = default)
    {
        return new ValueTask(_store.MutateAsync(context.RunId, report =>
        {
            report.Topology = topology
                .Select(x => new CaseTopologyEdge(x.Parent, x.Child))
                .ToList();

            if (report.EndedAt < report.StartedAt)
                report.EndedAt = DateTimeOffset.UtcNow;

            CaseProjectionMutations.RefreshDerivedFields(report);
        }, ct));
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
