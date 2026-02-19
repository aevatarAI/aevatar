using System.Collections.Concurrent;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.CQRS.Projection.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Application-facing facade for workflow run projection lifecycle and read-model queries.
/// </summary>
public sealed class WorkflowExecutionProjectionService : IWorkflowExecutionProjectionPort
{
    private readonly IProjectionRuntimeOptions _options;
    private readonly IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> _lifecycle;
    private readonly IProjectionReadModelStore<WorkflowExecutionReport, string> _store;
    private readonly IProjectionRunIdGenerator _runIdGenerator;
    private readonly IProjectionClock _clock;
    private readonly IWorkflowExecutionProjectionContextFactory _contextFactory;
    private readonly WorkflowExecutionReadModelMapper _mapper;
    private readonly ConcurrentDictionary<string, WorkflowExecutionProjectionContext> _contexts = new(StringComparer.Ordinal);

    public WorkflowExecutionProjectionService(
        IProjectionRuntimeOptions options,
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IProjectionReadModelStore<WorkflowExecutionReport, string> store,
        IProjectionRunIdGenerator runIdGenerator,
        IProjectionClock clock,
        IWorkflowExecutionProjectionContextFactory contextFactory,
        WorkflowExecutionReadModelMapper mapper)
    {
        _options = options;
        _lifecycle = lifecycle;
        _store = store;
        _runIdGenerator = runIdGenerator;
        _clock = clock;
        _contextFactory = contextFactory;
        _mapper = mapper;
    }

    public bool ProjectionEnabled => _options.Enabled;

    public bool EnableRunQueryEndpoints => _options.Enabled && _options.EnableRunQueryEndpoints;

    public async Task<WorkflowProjectionSession> StartAsync(
        string rootActorId,
        string workflowName,
        string input,
        IWorkflowRunEventSink sink,
        string? runId = null,
        CancellationToken ct = default)
    {
        var resolvedRunId = string.IsNullOrWhiteSpace(runId) ? _runIdGenerator.NextRunId() : runId;
        var startedAt = _clock.UtcNow;

        if (!ProjectionEnabled)
        {
            return new WorkflowProjectionSession
            {
                RunId = resolvedRunId,
                StartedAt = startedAt,
                Enabled = false,
            };
        }

        var context = _contextFactory.Create(resolvedRunId, rootActorId, workflowName, input, startedAt);
        context.SetRunEventSink(sink);
        await _lifecycle.StartAsync(context, ct);
        _contexts[resolvedRunId] = context;

        return new WorkflowProjectionSession
        {
            RunId = resolvedRunId,
            StartedAt = startedAt,
            Enabled = true,
        };
    }

    public async Task<WorkflowProjectionCompletionStatus> WaitForRunProjectionCompletionStatusAsync(
        string runId,
        TimeSpan? timeoutOverride = null,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled)
            return WorkflowProjectionCompletionStatus.Disabled;

        var timeout = timeoutOverride ?? TimeSpan.FromMilliseconds(Math.Max(1, _options.RunProjectionCompletionWaitTimeoutMs));
        var status = await _lifecycle.WaitForCompletionAsync(runId, timeout, ct);
        return ToProjectionCompletionStatus(status);
    }

    public async Task<WorkflowRunReport?> CompleteAsync(
        WorkflowProjectionSession session,
        IReadOnlyList<WorkflowRunTopologyEdge> topology,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || !session.Enabled)
            return null;

        if (_contexts.TryRemove(session.RunId, out var context))
        {
            var projectionTopology = topology.Select(x => new WorkflowExecutionTopologyEdge(x.Parent, x.Child)).ToList();
            await _lifecycle.CompleteAsync(context, projectionTopology, ct);
        }

        var report = await _store.GetAsync(session.RunId, ct);
        return report == null ? null : _mapper.ToReport(report);
    }

    public async Task<IReadOnlyList<WorkflowRunSummary>> ListRunsAsync(int take = 50, CancellationToken ct = default)
    {
        if (!EnableRunQueryEndpoints)
            return [];

        var reports = await _store.ListAsync(take, ct);
        return reports.Select(_mapper.ToSummary).ToList();
    }

    public async Task<WorkflowRunReport?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        if (!EnableRunQueryEndpoints)
            return null;

        var report = await _store.GetAsync(runId, ct);
        return report == null ? null : _mapper.ToReport(report);
    }

    private static WorkflowProjectionCompletionStatus ToProjectionCompletionStatus(ProjectionRunCompletionStatus status)
    {
        return status switch
        {
            ProjectionRunCompletionStatus.Completed => WorkflowProjectionCompletionStatus.Completed,
            ProjectionRunCompletionStatus.TimedOut => WorkflowProjectionCompletionStatus.TimedOut,
            ProjectionRunCompletionStatus.Failed => WorkflowProjectionCompletionStatus.Failed,
            ProjectionRunCompletionStatus.Stopped => WorkflowProjectionCompletionStatus.Stopped,
            ProjectionRunCompletionStatus.NotFound => WorkflowProjectionCompletionStatus.NotFound,
            ProjectionRunCompletionStatus.Disabled => WorkflowProjectionCompletionStatus.Disabled,
            _ => WorkflowProjectionCompletionStatus.Unknown,
        };
    }
}
