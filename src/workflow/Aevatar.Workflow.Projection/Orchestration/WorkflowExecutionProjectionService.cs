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
        _ = await EnsureActorContextAsync(
            rootActorId,
            workflowName,
            input,
            commandId,
            updateMetadata: true,
            ct);
    }

    private Task RefreshReportMetadataAsync(
        string actorId,
        WorkflowExecutionProjectionContext context,
        CancellationToken ct)
    {
        return _store.MutateAsync(actorId, report =>
        {
            report.CommandId = context.CommandId;
            report.WorkflowName = context.WorkflowName;
            report.Input = context.Input;
            report.StartedAt = context.StartedAt;
            if (report.EndedAt < report.StartedAt)
                report.EndedAt = report.StartedAt;

            report.DurationMs = Math.Max(0, (report.EndedAt - report.StartedAt).TotalMilliseconds);
        }, ct);
    }

    public async Task AttachLiveSinkAsync(
        string actorId,
        string commandId,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);

        if (!ProjectionEnabled || string.IsNullOrWhiteSpace(actorId))
            return;

        var context = await EnsureActorContextAsync(
            actorId,
            string.Empty,
            string.Empty,
            commandId,
            updateMetadata: false,
            ct);
        if (context != null)
            context.AttachLiveSink(commandId, sink);
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

    public async Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        if (!EnableActorQueryEndpoints)
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var reports = await _store.ListAsync(boundedTake, ct);
        return reports
            .Select(_mapper.ToActorSnapshot)
            .ToList();
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

    private async Task<WorkflowExecutionProjectionContext?> EnsureActorContextAsync(
        string rootActorId,
        string workflowName,
        string input,
        string commandId,
        bool updateMetadata,
        CancellationToken ct)
    {
        if (!ProjectionEnabled || string.IsNullOrWhiteSpace(rootActorId))
            return null;

        if (_contextsByActorId.TryGetValue(rootActorId, out var existingContext))
        {
            if (updateMetadata)
            {
                existingContext.UpdateRunMetadata(
                    commandId,
                    workflowName,
                    input,
                    _clock.UtcNow);
                await RefreshReportMetadataAsync(rootActorId, existingContext, ct);
            }

            return existingContext;
        }

        await _contextGate.WaitAsync(ct);
        try
        {
            if (_contextsByActorId.TryGetValue(rootActorId, out existingContext))
            {
                if (updateMetadata)
                {
                    existingContext.UpdateRunMetadata(
                        commandId,
                        workflowName,
                        input,
                        _clock.UtcNow);
                    await RefreshReportMetadataAsync(rootActorId, existingContext, ct);
                }

                return existingContext;
            }

            var startedAt = _clock.UtcNow;
            var context = _contextFactory.Create(
                rootActorId,
                commandId,
                rootActorId,
                workflowName,
                input,
                startedAt);
            await _lifecycle.StartAsync(context, ct);
            _contextsByActorId[rootActorId] = context;
            return context;
        }
        finally
        {
            _contextGate.Release();
        }
    }
}
