using Aevatar.Cqrs.Projections.Abstractions.ReadModels;
using Aevatar.Cqrs.Projections.Reducers;

namespace Aevatar.Cqrs.Projections.Projectors;

/// <summary>
/// EventEnvelope -> ChatRunReport projector.
/// </summary>
public sealed class ChatRunReadModelProjector : IChatRunProjector
{
    private readonly IProjectionReadModelStore<ChatRunReport, string> _store;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IProjectionEventReducer<ChatRunReport, ChatProjectionContext>>> _reducersByType;

    public ChatRunReadModelProjector(
        IProjectionReadModelStore<ChatRunReport, string> store,
        IEnumerable<IProjectionEventReducer<ChatRunReport, ChatProjectionContext>> reducers)
    {
        _store = store;
        _reducersByType = reducers
            .OrderBy(x => x.Order)
            .GroupBy(x => x.EventTypeUrl, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<IProjectionEventReducer<ChatRunReport, ChatProjectionContext>>)x.ToList(),
                StringComparer.Ordinal);
    }

    public int Order => 0;

    public ValueTask InitializeAsync(ChatProjectionContext context, CancellationToken ct = default)
    {
        var report = new ChatRunReport
        {
            ReportVersion = "1.0",
            WorkflowName = context.WorkflowName,
            RootActorId = context.RootActorId,
            RunId = context.RunId,
            StartedAt = context.StartedAt,
            EndedAt = context.StartedAt,
            Input = context.Input,
        };
        report.Summary = new ChatRunSummary();
        return new ValueTask(_store.UpsertAsync(report, ct));
    }

    public ValueTask ProjectAsync(ChatProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
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

            ChatRunProjectionMutations.RefreshDerivedFields(report);
        }, ct));
    }

    public ValueTask CompleteAsync(
        ChatProjectionContext context,
        IReadOnlyList<ChatTopologyEdge> topology,
        CancellationToken ct = default)
    {
        return new ValueTask(_store.MutateAsync(context.RunId, report =>
        {
            report.Topology = topology.Select(x => new ChatTopologyEdge(x.Parent, x.Child)).ToList();
            if (report.EndedAt < report.StartedAt)
                report.EndedAt = DateTimeOffset.UtcNow;
            ChatRunProjectionMutations.RefreshDerivedFields(report);
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
