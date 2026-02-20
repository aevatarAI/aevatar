using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.CQRS.Projection.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Application-facing facade for workflow run projection lifecycle and read-model queries.
/// </summary>
public sealed class WorkflowExecutionProjectionService : IWorkflowExecutionProjectionPort
{
    private const string ProjectionCoordinatorPublisherId = "workflow.projection.coordinator";

    private readonly IActorRuntime _runtime;
    private readonly WorkflowExecutionProjectionOptions _options;
    private readonly IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> _lifecycle;
    private readonly IProjectionReadModelStore<WorkflowExecutionReport, string> _store;
    private readonly IProjectionClock _clock;
    private readonly IWorkflowExecutionProjectionContextFactory _contextFactory;
    private readonly WorkflowExecutionReadModelMapper _mapper;

    public WorkflowExecutionProjectionService(
        IActorRuntime runtime,
        WorkflowExecutionProjectionOptions options,
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IProjectionReadModelStore<WorkflowExecutionReport, string> store,
        IProjectionClock clock,
        IWorkflowExecutionProjectionContextFactory contextFactory,
        WorkflowExecutionReadModelMapper mapper)
    {
        _runtime = runtime;
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
        var coordinatorActor = await ResolveProjectionCoordinatorActorAsync(rootActorId, ct);
        var envelope = CreateCoordinatorEnvelope(
            new WorkflowExecutionProjectionAcquireEvent
            {
                RootActorId = rootActorId,
                CommandId = commandId,
            },
            commandId);
        await coordinatorActor.HandleEventAsync(envelope, ct);
    }

    private async Task ReleaseProjectionOwnershipAsync(
        string rootActorId,
        string commandId,
        CancellationToken ct)
    {
        var coordinatorActor = await ResolveProjectionCoordinatorActorAsync(rootActorId, ct);
        var envelope = CreateCoordinatorEnvelope(
            new WorkflowExecutionProjectionReleaseEvent
            {
                RootActorId = rootActorId,
                CommandId = commandId,
            },
            commandId);
        await coordinatorActor.HandleEventAsync(envelope, ct);
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

    private async Task<IActor> ResolveProjectionCoordinatorActorAsync(string rootActorId, CancellationToken ct)
    {
        var coordinatorActorId = WorkflowExecutionProjectionCoordinatorGAgent.BuildActorId(rootActorId);
        var existing = await _runtime.GetAsync(coordinatorActorId);
        if (existing != null)
            return EnsureCoordinatorActorType(existing, coordinatorActorId);

        try
        {
            var created = await _runtime.CreateAsync<WorkflowExecutionProjectionCoordinatorGAgent>(coordinatorActorId, ct);
            return EnsureCoordinatorActorType(created, coordinatorActorId);
        }
        catch (InvalidOperationException)
        {
            // Another concurrent caller may have created it first.
            var raced = await _runtime.GetAsync(coordinatorActorId);
            if (raced != null)
                return EnsureCoordinatorActorType(raced, coordinatorActorId);

            throw;
        }
    }

    private static IActor EnsureCoordinatorActorType(IActor actor, string actorId)
    {
        if (actor.Agent is WorkflowExecutionProjectionCoordinatorGAgent)
            return actor;

        throw new InvalidOperationException(
            $"Actor '{actorId}' is not a workflow projection coordinator actor.");
    }

    private static EventEnvelope CreateCoordinatorEnvelope(IMessage payload, string correlationId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            PublisherId = ProjectionCoordinatorPublisherId,
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
        };

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
