using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Reducers;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Projectors;

/// <summary>
/// EventEnvelope -> WorkflowExecutionReport projector.
/// </summary>
public sealed class WorkflowExecutionReadModelProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
{
    private readonly IProjectionReadModelStore<WorkflowExecutionReport, string> _store;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>>> _reducersByType;
    private readonly bool _enableRunEventIsolation;
    private readonly IReadOnlyList<IWorkflowExecutionRunIdResolver> _runIdResolvers;

    public WorkflowExecutionReadModelProjector(
        IProjectionReadModelStore<WorkflowExecutionReport, string> store,
        IEnumerable<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>> reducers,
        IEnumerable<IWorkflowExecutionRunIdResolver>? runIdResolvers = null,
        WorkflowExecutionProjectionOptions? options = null)
    {
        _store = store;
        _enableRunEventIsolation = options?.EnableRunEventIsolation == true;
        _runIdResolvers = (runIdResolvers ?? [])
            .OrderBy(x => x.Order)
            .ToList();
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
            ProjectionScope = _enableRunEventIsolation
                ? WorkflowExecutionProjectionScope.RunIsolated
                : WorkflowExecutionProjectionScope.ActorShared,
            TopologySource = WorkflowExecutionTopologySource.RuntimeSnapshot,
            CompletionStatus = WorkflowExecutionCompletionStatus.Running,
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
        if (_enableRunEventIsolation && !IsEnvelopeForCurrentRun(context, envelope))
            return ValueTask.CompletedTask;

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
            report.TopologySource = WorkflowExecutionTopologySource.RuntimeSnapshot;
            if (report.EndedAt < report.StartedAt)
                report.EndedAt = DateTimeOffset.UtcNow;
            if (report.CompletionStatus == WorkflowExecutionCompletionStatus.Running)
                report.CompletionStatus = WorkflowExecutionCompletionStatus.Completed;
            WorkflowExecutionProjectionMutations.RefreshDerivedFields(report);
        }, ct));
    }

    private bool IsEnvelopeForCurrentRun(WorkflowExecutionProjectionContext context, EventEnvelope envelope)
    {
        if (!TryResolveRunId(envelope, out var runId))
            return true;

        return string.Equals(runId, context.RunId, StringComparison.Ordinal);
    }

    private bool TryResolveRunId(EventEnvelope envelope, out string? runId)
    {
        foreach (var resolver in _runIdResolvers)
        {
            if (resolver.TryResolve(envelope, out runId))
                return true;
        }

        runId = null;
        return false;
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
