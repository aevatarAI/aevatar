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

    public async Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
        string rootActorId,
        string workflowName,
        string input,
        string commandId,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || string.IsNullOrWhiteSpace(rootActorId))
            return null;

        await _contextGate.WaitAsync(ct);
        try
        {
            var existingReport = await _store.GetAsync(rootActorId, ct);
            if (existingReport?.CompletionStatus == WorkflowExecutionCompletionStatus.Running)
                throw new InvalidOperationException($"Projection for actor '{rootActorId}' is already active.");

            var startedAt = _clock.UtcNow;
            var context = _contextFactory.Create(
                rootActorId,
                commandId,
                rootActorId,
                workflowName,
                input,
                startedAt);

            await _lifecycle.StartAsync(context, ct);
            await RefreshReportMetadataAsync(rootActorId, context, ct);
            return new WorkflowExecutionProjectionLease(context);
        }
        finally
        {
            _contextGate.Release();
        }
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
        IWorkflowExecutionProjectionLease lease,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled)
            return;

        var runtimeLease = ResolveRuntimeLease(lease);
        runtimeLease.Context.AttachLiveSink(runtimeLease.CommandId, sink);
    }

    public Task DetachLiveSinkAsync(
        IWorkflowExecutionProjectionLease lease,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled)
            return Task.CompletedTask;

        var runtimeLease = ResolveRuntimeLease(lease);
        runtimeLease.Context.DetachLiveSink(sink);
        return Task.CompletedTask;
    }

    public async Task ReleaseActorProjectionAsync(
        IWorkflowExecutionProjectionLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();
        if (!ProjectionEnabled)
            return;

        var runtimeLease = ResolveRuntimeLease(lease);
        var context = runtimeLease.Context;

        if (context.GetLiveSinksSnapshot().Count > 0)
            return;

        await _lifecycle.StopAsync(context, ct);
        await MarkProjectionStoppedAsync(context.RootActorId, ct);
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

    private static WorkflowExecutionProjectionLease ResolveRuntimeLease(IWorkflowExecutionProjectionLease lease) =>
        lease as WorkflowExecutionProjectionLease
        ?? throw new InvalidOperationException("Unsupported workflow projection lease implementation.");

    private sealed class WorkflowExecutionProjectionLease : IWorkflowExecutionProjectionLease
    {
        public WorkflowExecutionProjectionLease(WorkflowExecutionProjectionContext context)
        {
            Context = context;
            ActorId = context.RootActorId;
            CommandId = context.CommandId;
        }

        public string ActorId { get; }
        public string CommandId { get; }
        public WorkflowExecutionProjectionContext Context { get; }
    }

    private Task MarkProjectionStoppedAsync(string actorId, CancellationToken ct)
    {
        return _store.MutateAsync(actorId, report =>
        {
            if (report.CompletionStatus == WorkflowExecutionCompletionStatus.Running)
                report.CompletionStatus = WorkflowExecutionCompletionStatus.Stopped;

            if (report.EndedAt < report.StartedAt)
                report.EndedAt = _clock.UtcNow;

            report.DurationMs = Math.Max(0, (report.EndedAt - report.StartedAt).TotalMilliseconds);
        }, ct);
    }
}
