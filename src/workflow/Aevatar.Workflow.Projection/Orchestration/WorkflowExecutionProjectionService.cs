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
    private readonly WorkflowExecutionProjectionOptions _options;
    private readonly IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> _lifecycle;
    private readonly IProjectionReadModelStore<WorkflowExecutionReport, string> _store;
    private readonly IProjectionClock _clock;
    private readonly IWorkflowExecutionProjectionContextFactory _contextFactory;
    private readonly WorkflowExecutionReadModelMapper _mapper;
    private readonly ConcurrentDictionary<string, WorkflowExecutionProjectionContext> _contextsByActorId = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _contextGate = new(1, 1);

    public WorkflowExecutionProjectionService(
        WorkflowExecutionProjectionOptions options,
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IProjectionReadModelStore<WorkflowExecutionReport, string> store,
        IProjectionClock clock,
        IWorkflowExecutionProjectionContextFactory contextFactory,
        WorkflowExecutionReadModelMapper mapper)
    {
        _options = options;
        _lifecycle = lifecycle;
        _store = store;
        _clock = clock;
        _contextFactory = contextFactory;
        _mapper = mapper;
    }

    public bool ProjectionEnabled => _options.Enabled;

    public bool EnableActorQueryEndpoints => _options.Enabled && _options.EnableActorQueryEndpoints;

    public async Task EnsureActorProjectionAsync(
        string rootActorId,
        string workflowName,
        string input,
        string commandId,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || string.IsNullOrWhiteSpace(rootActorId))
            return;
        if (_contextsByActorId.ContainsKey(rootActorId))
            return;

        await _contextGate.WaitAsync(ct);
        try
        {
            if (_contextsByActorId.ContainsKey(rootActorId))
                return;

            var startedAt = _clock.UtcNow;
            var projectionId = rootActorId;
            var context = _contextFactory.Create(
                projectionId,
                commandId,
                rootActorId,
                workflowName,
                input,
                startedAt);
            await _lifecycle.StartAsync(context, ct);
            _contextsByActorId[rootActorId] = context;
        }
        finally
        {
            _contextGate.Release();
        }
    }

    public async Task AttachLiveSinkAsync(
        string actorId,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);

        if (!ProjectionEnabled || string.IsNullOrWhiteSpace(actorId))
            return;

        await EnsureActorProjectionAsync(actorId, string.Empty, string.Empty, string.Empty, ct);
        if (_contextsByActorId.TryGetValue(actorId, out var context))
            context.AttachLiveSink(sink);
    }

    public Task DetachLiveSinkAsync(
        string actorId,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled || string.IsNullOrWhiteSpace(actorId))
            return Task.CompletedTask;

        if (_contextsByActorId.TryGetValue(actorId, out var context))
            context.DetachLiveSink(sink);
        return Task.CompletedTask;
    }

    public async Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(
        string actorId,
        CancellationToken ct = default)
    {
        if (!EnableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return null;

        var report = await _store.GetAsync(actorId, ct);
        if (report == null)
            return null;

        return _mapper.ToActorSnapshot(report);
    }

    public async Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(
        string actorId,
        int take = 200,
        CancellationToken ct = default)
    {
        if (!EnableActorQueryEndpoints || string.IsNullOrWhiteSpace(actorId))
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var report = await _store.GetAsync(actorId, ct);
        if (report == null)
            return [];

        var timeline = report.Timeline
            .OrderByDescending(x => x.Timestamp)
            .Take(boundedTake)
            .Select(_mapper.ToActorTimelineItem)
            .ToList();
        return timeline;
    }
}
