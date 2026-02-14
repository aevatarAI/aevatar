using Aevatar.CQRS.Projections.Abstractions.ReadModels;
using Aevatar.CQRS.Projections.Reducers;

namespace Aevatar.CQRS.Projections.Projectors;

/// <summary>
/// EventEnvelope -> WorkflowExecutionReport projector.
/// </summary>
public sealed class WorkflowExecutionReadModelProjector : IWorkflowExecutionProjector
{
    private readonly IProjectionReadModelStore<WorkflowExecutionReport, string> _store;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>>> _reducersByType;

    public WorkflowExecutionReadModelProjector(
        IProjectionReadModelStore<WorkflowExecutionReport, string> store,
        IEnumerable<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>> reducers)
    {
        _store = store;
        _reducersByType = reducers
            .OrderBy(x => x.Order)
            .GroupBy(x => x.EventTypeUrl, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>>)x.ToList(),
                StringComparer.Ordinal);
    }

    public int Order => 0;

    public ValueTask InitializeAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
    {
        var report = new WorkflowExecutionReport
        {
            ReportVersion = "1.0",
            WorkflowName = context.WorkflowName,
            RootActorId = context.RootActorId,
            RunId = context.RunId,
            StartedAt = context.StartedAt,
            EndedAt = context.StartedAt,
            Input = context.Input,
        };
        report.Summary = new WorkflowExecutionSummary();
        return new ValueTask(_store.UpsertAsync(report, ct));
    }

    public ValueTask ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
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

            WorkflowExecutionProjectionMutations.RefreshDerivedFields(report);
        }, ct));
    }

    public ValueTask CompleteAsync(
        WorkflowExecutionProjectionContext context,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        CancellationToken ct = default)
    {
        return new ValueTask(_store.MutateAsync(context.RunId, report =>
        {
            report.Topology = topology.Select(x => new WorkflowExecutionTopologyEdge(x.Parent, x.Child)).ToList();
            if (report.EndedAt < report.StartedAt)
                report.EndedAt = DateTimeOffset.UtcNow;
            WorkflowExecutionProjectionMutations.RefreshDerivedFields(report);
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
