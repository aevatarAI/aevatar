using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Reducers;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.Deduplication;

namespace Aevatar.Workflow.Projection.Projectors;

/// <summary>
/// EventEnvelope -> WorkflowExecutionReport projector.
/// </summary>
public sealed class WorkflowExecutionReadModelProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
{
    private readonly IProjectionStoreDispatcher<WorkflowExecutionReport, string> _storeDispatcher;
    private readonly IEventDeduplicator _deduplicator;
    private readonly IProjectionClock _clock;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>>> _reducersByType;

    public WorkflowExecutionReadModelProjector(
        IProjectionStoreDispatcher<WorkflowExecutionReport, string> storeDispatcher,
        IEventDeduplicator deduplicator,
        IProjectionClock clock,
        IEnumerable<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>> reducers)
    {
        _storeDispatcher = storeDispatcher;
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
            ProjectionScope = WorkflowExecutionProjectionScope.RunIsolated,
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
        return new ValueTask(_storeDispatcher.UpsertAsync(report, ct));
    }

    public async ValueTask ProjectAsync(WorkflowExecutionProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        var normalized = ProjectionEnvelopeNormalizer.Normalize(envelope);
        if (normalized == null)
            return;

        var typeUrl = normalized.Payload?.TypeUrl;
        if (string.IsNullOrWhiteSpace(typeUrl))
            return;
        if (!_reducersByType.TryGetValue(typeUrl, out var reducers))
            return;
        if (!string.IsNullOrWhiteSpace(normalized.Id))
        {
            var dedupKey = $"{context.RootActorId}:{normalized.Id}";
            if (!await _deduplicator.TryRecordAsync(dedupKey))
                return;
        }

        var now = ProjectionEnvelopeTimestampResolver.Resolve(normalized, _clock.UtcNow);
        await _storeDispatcher.MutateAsync(context.RootActorId, report =>
        {
            report.Id = context.RootActorId;
            if (string.IsNullOrWhiteSpace(report.RootActorId))
                report.RootActorId = context.RootActorId;
            var mutated = false;
            foreach (var reducer in reducers)
                mutated |= reducer.Reduce(report, context, normalized, now);

            if (!mutated)
                return;

            WorkflowExecutionProjectionMutations.RecordProjectedEvent(report, normalized);
            WorkflowExecutionProjectionMutations.RefreshDerivedFields(report, now);
        }, ct);
    }

    public ValueTask CompleteAsync(
        WorkflowExecutionProjectionContext context,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        CancellationToken ct = default)
    {
        var completedAt = _clock.UtcNow;
        return new ValueTask(_storeDispatcher.MutateAsync(context.RootActorId, report =>
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

}
