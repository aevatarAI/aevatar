using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Reducers;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Core;
using AIEvents = Aevatar.AI.Abstractions;

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

    public WorkflowExecutionReadModelProjector(
        IProjectionReadModelStore<WorkflowExecutionReport, string> store,
        IEnumerable<IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>> reducers,
        WorkflowExecutionProjectionOptions? options = null)
    {
        _store = store;
        _enableRunEventIsolation = options?.EnableRunEventIsolation == true;
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
            ProjectionScope = _enableRunEventIsolation ? "run_isolated" : "actor_shared",
            TopologySource = "runtime_snapshot",
            CompletionStatus = "running",
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
            report.TopologySource = "runtime_snapshot";
            if (report.EndedAt < report.StartedAt)
                report.EndedAt = DateTimeOffset.UtcNow;
            if (string.Equals(report.CompletionStatus, "running", StringComparison.Ordinal))
                report.CompletionStatus = "completed";
            WorkflowExecutionProjectionMutations.RefreshDerivedFields(report);
        }, ct));
    }

    private static bool IsEnvelopeForCurrentRun(WorkflowExecutionProjectionContext context, EventEnvelope envelope)
    {
        if (!TryResolveRunId(envelope, out var runId))
            return true;

        return string.Equals(runId, context.RunId, StringComparison.Ordinal);
    }

    private static bool TryResolveRunId(EventEnvelope envelope, out string? runId)
    {
        runId = null;
        var payload = envelope.Payload;
        if (payload == null)
            return false;

        if (payload.Is(StartWorkflowEvent.Descriptor))
        {
            runId = payload.Unpack<StartWorkflowEvent>().RunId;
            return !string.IsNullOrWhiteSpace(runId);
        }

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            runId = payload.Unpack<StepRequestEvent>().RunId;
            return !string.IsNullOrWhiteSpace(runId);
        }

        if (payload.Is(StepCompletedEvent.Descriptor))
        {
            runId = payload.Unpack<StepCompletedEvent>().RunId;
            return !string.IsNullOrWhiteSpace(runId);
        }

        if (payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            runId = payload.Unpack<WorkflowCompletedEvent>().RunId;
            return !string.IsNullOrWhiteSpace(runId);
        }

        if (payload.Is(AIEvents.TextMessageStartEvent.Descriptor))
            return TryResolveRunIdFromSession(payload.Unpack<AIEvents.TextMessageStartEvent>().SessionId, out runId);

        if (payload.Is(AIEvents.TextMessageContentEvent.Descriptor))
            return TryResolveRunIdFromSession(payload.Unpack<AIEvents.TextMessageContentEvent>().SessionId, out runId);

        if (payload.Is(AIEvents.TextMessageEndEvent.Descriptor))
            return TryResolveRunIdFromSession(payload.Unpack<AIEvents.TextMessageEndEvent>().SessionId, out runId);

        if (payload.Is(AIEvents.ChatResponseEvent.Descriptor))
            return TryResolveRunIdFromSession(payload.Unpack<AIEvents.ChatResponseEvent>().SessionId, out runId);

        return false;
    }

    private static bool TryResolveRunIdFromSession(string? sessionId, out string? runId)
    {
        runId = null;
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        var separatorIndex = sessionId.IndexOf(':');
        if (separatorIndex <= 0)
            return false;

        runId = sessionId[..separatorIndex];
        return !string.IsNullOrWhiteSpace(runId);
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
