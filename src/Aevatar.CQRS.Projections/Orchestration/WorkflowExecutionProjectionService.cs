using Aevatar.CQRS.Projections.Abstractions.ReadModels;
using Aevatar.CQRS.Projections.Configuration;

namespace Aevatar.CQRS.Projections.Orchestration;

/// <summary>
/// Application-facing facade for chat run projection lifecycle and read-model queries.
/// </summary>
public sealed class WorkflowExecutionProjectionService : IWorkflowExecutionProjectionService
{
    private readonly WorkflowExecutionProjectionOptions _options;
    private readonly IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> _lifecycle;
    private readonly IProjectionReadModelStore<WorkflowExecutionReport, string> _store;

    public WorkflowExecutionProjectionService(
        WorkflowExecutionProjectionOptions options,
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IProjectionReadModelStore<WorkflowExecutionReport, string> store)
    {
        _options = options;
        _lifecycle = lifecycle;
        _store = store;
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
        var runId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;

        if (!ProjectionEnabled)
        {
            return new WorkflowExecutionProjectionSession
            {
                RunId = runId,
                StartedAt = startedAt,
                Context = null,
            };
        }

        var context = new WorkflowExecutionProjectionContext
        {
            RunId = runId,
            RootActorId = rootActorId,
            WorkflowName = workflowName,
            StartedAt = startedAt,
            Input = input,
        };

        await _lifecycle.StartAsync(context, ct);

        return new WorkflowExecutionProjectionSession
        {
            RunId = runId,
            StartedAt = startedAt,
            Context = context,
        };
    }

    public Task ProjectAsync(
        WorkflowExecutionProjectionSession session,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || session.Context == null)
            return Task.CompletedTask;

        return _lifecycle.ProjectAsync(session.Context, envelope, ct);
    }

    public Task<bool> WaitForRunProjectionCompletedAsync(string runId, CancellationToken ct = default)
    {
        if (!ProjectionEnabled)
            return Task.FromResult(false);

        var waitMs = Math.Max(1, _options.RunProjectionCompletionWaitTimeoutMs);
        return _lifecycle.WaitForCompletionAsync(runId, TimeSpan.FromMilliseconds(waitMs), ct);
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
