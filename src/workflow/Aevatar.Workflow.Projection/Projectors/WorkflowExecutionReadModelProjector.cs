using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Reducers;
using Aevatar.Foundation.Abstractions.Deduplication;

namespace Aevatar.Workflow.Projection.Projectors;

/// <summary>
/// EventEnvelope -> WorkflowExecutionReport projector.
/// </summary>
public sealed class WorkflowExecutionReadModelProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
{
    private readonly IProjectionMaterializationRouter<WorkflowExecutionReport, string> _materializationRouter;
    private readonly IEventDeduplicator _deduplicator;
    private readonly IProjectionClock _clock;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>>> _reducersByType;

    public WorkflowExecutionReadModelProjector(
        IProjectionMaterializationRouter<WorkflowExecutionReport, string> materializationRouter,
        IEventDeduplicator deduplicator,
        IProjectionClock clock,
        IEnumerable<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>> reducers)
    {
        _materializationRouter = materializationRouter;
        _deduplicator = deduplicator;
        _clock = clock;
        _reducersByType = reducers
            .GroupBy(x => x.EventTypeUrl, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>>)x.ToList(),
                StringComparer.Ordinal);
    }

    public ValueTask InitializeAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
    {
        var report = new WorkflowExecutionReport
        {
            Id = context.RootActorId,
            ReportVersion = "1.0",
            ProjectionScope = WorkflowExecutionProjectionScope.ActorShared,
            TopologySource = WorkflowExecutionTopologySource.RuntimeSnapshot,
            CompletionStatus = WorkflowExecutionCompletionStatus.Running,
            WorkflowName = context.WorkflowName,
            RootActorId = context.RootActorId,
            CommandId = context.CommandId,
            CreatedAt = context.StartedAt,
            UpdatedAt = context.StartedAt,
            StartedAt = context.StartedAt,
            EndedAt = context.StartedAt,
            Input = context.Input,
        };
        report.Summary = new WorkflowExecutionSummary();
        return new ValueTask(_materializationRouter.UpsertAsync(report, ct));
    }

    public async ValueTask ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        var typeUrl = envelope.Payload?.TypeUrl;
        if (string.IsNullOrWhiteSpace(typeUrl))
            return;
        if (!_reducersByType.TryGetValue(typeUrl, out var reducers))
            return;
        if (!string.IsNullOrWhiteSpace(envelope.Id))
        {
            var dedupKey = $"{context.RootActorId}:{envelope.Id}";
            if (!await _deduplicator.TryRecordAsync(dedupKey))
                return;
        }

        var now = ResolveEventTimestamp(envelope, _clock.UtcNow);
        await _materializationRouter.MutateAsync(context.RootActorId, report =>
        {
            report.Id = context.RootActorId;
            if (string.IsNullOrWhiteSpace(report.RootActorId))
                report.RootActorId = context.RootActorId;
            var mutated = false;
            foreach (var reducer in reducers)
                mutated |= reducer.Reduce(report, context, envelope, now);

            if (!mutated)
                return;

            WorkflowExecutionProjectionMutations.RecordProjectedEvent(report, envelope);
            WorkflowExecutionProjectionMutations.RefreshDerivedFields(report, now);
        }, ct);
    }

    public ValueTask CompleteAsync(
        WorkflowExecutionProjectionContext context,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        CancellationToken ct = default)
    {
        var completedAt = _clock.UtcNow;
        return new ValueTask(_materializationRouter.MutateAsync(context.RootActorId, report =>
        {
            report.Id = context.RootActorId;
            if (string.IsNullOrWhiteSpace(report.RootActorId))
                report.RootActorId = context.RootActorId;
            report.Topology = topology.Select(x => new WorkflowExecutionTopologyEdge(x.Parent, x.Child)).ToList();
            report.TopologySource = WorkflowExecutionTopologySource.RuntimeSnapshot;
            if (report.EndedAt < report.StartedAt)
                report.EndedAt = completedAt;
            if (report.CompletionStatus == WorkflowExecutionCompletionStatus.Running)
                report.CompletionStatus = WorkflowExecutionCompletionStatus.Completed;
            WorkflowExecutionProjectionMutations.RefreshDerivedFields(report, completedAt);
        }, ct));
    }

    private static DateTimeOffset ResolveEventTimestamp(EventEnvelope envelope, DateTimeOffset fallbackUtcNow)
    {
        var ts = envelope.Timestamp;
        if (ts == null)
            return fallbackUtcNow;

        var dt = ts.ToDateTime();
        if (dt.Kind != DateTimeKind.Utc)
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return new DateTimeOffset(dt);
    }
}
