using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.CQRS.Projection.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Application-facing facade for chat run projection lifecycle and read-model queries.
/// </summary>
public sealed class WorkflowExecutionProjectionService : IWorkflowExecutionProjectionService
{
    private readonly IProjectionRuntimeOptions _options;
    private readonly IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> _lifecycle;
    private readonly IProjectionReadModelStore<WorkflowExecutionReport, string> _store;
    private readonly IProjectionRunIdGenerator _runIdGenerator;
    private readonly IProjectionClock _clock;
    private readonly IWorkflowExecutionProjectionContextFactory _contextFactory;

    public WorkflowExecutionProjectionService(
        IProjectionRuntimeOptions options,
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IProjectionReadModelStore<WorkflowExecutionReport, string> store,
        IProjectionRunIdGenerator runIdGenerator,
        IProjectionClock clock,
        IWorkflowExecutionProjectionContextFactory contextFactory)
    {
        _options = options;
        _lifecycle = lifecycle;
        _store = store;
        _runIdGenerator = runIdGenerator;
        _clock = clock;
        _contextFactory = contextFactory;
    }

    public bool ProjectionEnabled => _options.Enabled;

    public bool EnableRunQueryEndpoints => _options.Enabled && _options.EnableRunQueryEndpoints;

    public bool EnableRunReportArtifacts => _options.Enabled && _options.EnableRunReportArtifacts;

    public async Task<WorkflowExecutionProjectionSession> StartAsync(
        string rootActorId,
        string workflowName,
        string input,
        CancellationToken ct = default)
    {
        var runId = _runIdGenerator.NextRunId();
        var startedAt = _clock.UtcNow;

        if (!ProjectionEnabled)
        {
            return new WorkflowExecutionProjectionSession
            {
                RunId = runId,
                StartedAt = startedAt,
                Context = null,
            };
        }

        var context = _contextFactory.Create(runId, rootActorId, workflowName, input, startedAt);

        await _lifecycle.StartAsync(context, ct);

        return new WorkflowExecutionProjectionSession
        {
            RunId = runId,
            StartedAt = startedAt,
            Context = context,
        };
    }

    public Task<ProjectionRunCompletionStatus> WaitForRunProjectionCompletionStatusAsync(
        string runId,
        TimeSpan? timeoutOverride = null,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled)
            return Task.FromResult(ProjectionRunCompletionStatus.Disabled);

        var timeout = timeoutOverride ?? TimeSpan.FromMilliseconds(Math.Max(1, _options.RunProjectionCompletionWaitTimeoutMs));
        return _lifecycle.WaitForCompletionAsync(runId, timeout, ct);
    }

    public async Task<WorkflowExecutionReport?> CompleteAsync(
        WorkflowExecutionProjectionSession session,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || session.Context == null)
            return null;

        await _lifecycle.CompleteAsync(session.Context, topology, ct);
        return await _store.GetAsync(session.RunId, ct);
    }

    public async Task<IReadOnlyList<WorkflowExecutionReport>> ListRunsAsync(int take = 50, CancellationToken ct = default)
    {
        if (!EnableRunQueryEndpoints)
            return [];

        return await _store.ListAsync(take, ct);
    }

    public async Task<WorkflowExecutionReport?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        if (!EnableRunQueryEndpoints)
            return null;

        return await _store.GetAsync(runId, ct);
    }
}
