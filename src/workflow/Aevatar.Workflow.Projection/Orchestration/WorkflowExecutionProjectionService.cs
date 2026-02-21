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
    private const string SinkBackpressureErrorCode = "RUN_SINK_BACKPRESSURE";
    private const string SinkWriteErrorCode = "RUN_SINK_WRITE_FAILED";
    private readonly IProjectionOwnershipCoordinator _ownershipCoordinator;
    private readonly WorkflowExecutionProjectionOptions _options;
    private readonly IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> _lifecycle;
    private readonly IProjectionReadModelStore<WorkflowExecutionReport, string> _store;
    private readonly IProjectionClock _clock;
    private readonly IWorkflowExecutionProjectionContextFactory _contextFactory;
    private readonly IProjectionSessionEventHub<WorkflowRunEvent> _runEventStreamHub;
    private readonly WorkflowExecutionReadModelMapper _mapper;

    public WorkflowExecutionProjectionService(
        IProjectionOwnershipCoordinator ownershipCoordinator,
        WorkflowExecutionProjectionOptions options,
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IProjectionReadModelStore<WorkflowExecutionReport, string> store,
        IProjectionClock clock,
        IWorkflowExecutionProjectionContextFactory contextFactory,
        IProjectionSessionEventHub<WorkflowRunEvent> runEventStreamHub,
        WorkflowExecutionReadModelMapper mapper)
    {
        _ownershipCoordinator = ownershipCoordinator;
        _options = options;
        _lifecycle = lifecycle;
        _store = store;
        _clock = clock;
        _contextFactory = contextFactory;
        _runEventStreamHub = runEventStreamHub;
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

        await AcquireProjectionOwnershipAsync(rootActorId, commandId, ct);
        try
        {
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
        catch
        {
            await TryReleaseProjectionOwnershipAsync(rootActorId, commandId);
            throw;
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
        var streamSubscription = await _runEventStreamHub.SubscribeAsync(
            runtimeLease.ActorId,
            runtimeLease.CommandId,
            evt => ForwardLiveEventAsync(runtimeLease, sink, evt),
            ct);

        var previous = runtimeLease.AttachOrReplaceLiveSinkSubscription(sink, streamSubscription);
        if (previous != null)
            await previous.DisposeAsync();
    }

    public async Task DetachLiveSinkAsync(
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
        await DetachLiveSinkSubscriptionAsync(runtimeLease, sink);
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

        if (runtimeLease.GetLiveSinkSubscriptionCount() > 0)
            return;

        await _lifecycle.StopAsync(context, ct);
        await MarkProjectionStoppedAsync(context.RootActorId, ct);
        await ReleaseProjectionOwnershipAsync(context.RootActorId, runtimeLease.CommandId, ct);
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

    private async Task AcquireProjectionOwnershipAsync(
        string rootActorId,
        string commandId,
        CancellationToken ct)
    {
        await _ownershipCoordinator.AcquireAsync(rootActorId, commandId, ct);
    }

    private async Task ReleaseProjectionOwnershipAsync(
        string rootActorId,
        string commandId,
        CancellationToken ct)
    {
        await _ownershipCoordinator.ReleaseAsync(rootActorId, commandId, ct);
    }

    private async Task TryReleaseProjectionOwnershipAsync(string rootActorId, string commandId)
    {
        try
        {
            await ReleaseProjectionOwnershipAsync(rootActorId, commandId, CancellationToken.None);
        }
        catch
        {
            // Best effort cleanup: ownership may already be released or unavailable.
        }
    }

    private sealed class WorkflowExecutionProjectionLease : IWorkflowExecutionProjectionLease
    {
        private readonly object _liveSinkGate = new();
        private readonly List<LiveSinkSubscription> _liveSinkSubscriptions = [];

        public WorkflowExecutionProjectionLease(WorkflowExecutionProjectionContext context)
        {
            Context = context;
            ActorId = context.RootActorId;
            CommandId = context.CommandId;
        }

        public string ActorId { get; }
        public string CommandId { get; }
        public WorkflowExecutionProjectionContext Context { get; }

        public IAsyncDisposable? AttachOrReplaceLiveSinkSubscription(
            IWorkflowRunEventSink sink,
            IAsyncDisposable streamSubscription)
        {
            ArgumentNullException.ThrowIfNull(sink);
            ArgumentNullException.ThrowIfNull(streamSubscription);

            lock (_liveSinkGate)
            {
                var index = _liveSinkSubscriptions.FindIndex(x => ReferenceEquals(x.Sink, sink));
                if (index < 0)
                {
                    _liveSinkSubscriptions.Add(new LiveSinkSubscription(sink, streamSubscription));
                    return null;
                }

                var previous = _liveSinkSubscriptions[index].StreamSubscription;
                _liveSinkSubscriptions[index] = new LiveSinkSubscription(sink, streamSubscription);
                return previous;
            }
        }

        public IAsyncDisposable? DetachLiveSinkSubscription(IWorkflowRunEventSink sink)
        {
            ArgumentNullException.ThrowIfNull(sink);

            lock (_liveSinkGate)
            {
                var index = _liveSinkSubscriptions.FindIndex(x => ReferenceEquals(x.Sink, sink));
                if (index < 0)
                    return null;

                var subscription = _liveSinkSubscriptions[index].StreamSubscription;
                _liveSinkSubscriptions.RemoveAt(index);
                return subscription;
            }
        }

        public int GetLiveSinkSubscriptionCount()
        {
            lock (_liveSinkGate)
                return _liveSinkSubscriptions.Count;
        }

        private sealed record LiveSinkSubscription(
            IWorkflowRunEventSink Sink,
            IAsyncDisposable StreamSubscription);
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

    private async ValueTask ForwardLiveEventAsync(
        WorkflowExecutionProjectionLease runtimeLease,
        IWorkflowRunEventSink sink,
        WorkflowRunEvent evt)
    {
        try
        {
            await sink.PushAsync(evt, CancellationToken.None);
        }
        catch (WorkflowRunEventSinkBackpressureException ex)
        {
            await DetachLiveSinkSubscriptionAsync(runtimeLease, sink);
            await PublishSinkFailureAsync(runtimeLease, SinkBackpressureErrorCode, ex.Message, evt);
        }
        catch (WorkflowRunEventSinkCompletedException)
        {
            await DetachLiveSinkSubscriptionAsync(runtimeLease, sink);
        }
        catch (InvalidOperationException ex)
        {
            await DetachLiveSinkSubscriptionAsync(runtimeLease, sink);
            await PublishSinkFailureAsync(runtimeLease, SinkWriteErrorCode, ex.Message, evt);
        }
    }

    private static async Task DetachLiveSinkSubscriptionAsync(
        WorkflowExecutionProjectionLease runtimeLease,
        IWorkflowRunEventSink sink)
    {
        var streamSubscription = runtimeLease.DetachLiveSinkSubscription(sink);
        if (streamSubscription != null)
            await streamSubscription.DisposeAsync();
    }

    private async Task PublishSinkFailureAsync(
        WorkflowExecutionProjectionLease runtimeLease,
        string code,
        string message,
        WorkflowRunEvent sourceEvent)
    {
        if (string.IsNullOrWhiteSpace(runtimeLease.ActorId) || string.IsNullOrWhiteSpace(runtimeLease.CommandId))
            return;

        var evtType = sourceEvent.Type;
        var runError = new WorkflowRunErrorEvent
        {
            Code = code,
            Message = $"Live sink delivery failed. eventType={evtType}, reason={message}",
            Timestamp = _clock.UtcNow.ToUnixTimeMilliseconds(),
        };

        try
        {
            await _runEventStreamHub.PublishAsync(
                runtimeLease.ActorId,
                runtimeLease.CommandId,
                runError,
                CancellationToken.None);
        }
        catch
        {
            // Best-effort telemetry path; do not fail run processing on secondary publish errors.
        }
    }
}
