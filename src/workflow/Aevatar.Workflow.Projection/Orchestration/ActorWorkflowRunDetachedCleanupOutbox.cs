using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class ActorWorkflowRunDetachedCleanupOutbox
    : IWorkflowRunDetachedCleanupOutbox,
      IWorkflowRunDetachedCleanupScheduler
{
    private const string OutboxPublisherId = "workflow.run.detached.cleanup.outbox";
    private const string DefaultScopeId = "workflow";
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAgentTypeVerifier _agentTypeVerifier;
    private readonly ILogger<ActorWorkflowRunDetachedCleanupOutbox> _logger;

    public ActorWorkflowRunDetachedCleanupOutbox(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IAgentTypeVerifier agentTypeVerifier,
        ILogger<ActorWorkflowRunDetachedCleanupOutbox>? logger = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _agentTypeVerifier = agentTypeVerifier ?? throw new ArgumentNullException(nameof(agentTypeVerifier));
        _logger = logger ?? NullLogger<ActorWorkflowRunDetachedCleanupOutbox>.Instance;
    }

    public async Task ScheduleAsync(
        WorkflowRunDetachedCleanupRequest request,
        CancellationToken ct = default)
    {
        await EnqueueAsync(request, ct);
        try
        {
            await TriggerReplayAsync(batchSize: 1, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Detached workflow cleanup replay trigger failed after enqueue. actorId={ActorId}, commandId={CommandId}",
                request.ActorId,
                request.CommandId);
        }
    }

    public async Task EnqueueAsync(
        WorkflowRunDetachedCleanupRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = await ResolveOutboxActorAsync(ct);
        var envelope = CreateEnvelope(
            new WorkflowRunDetachedCleanupEnqueuedEvent
            {
                RecordId = WorkflowRunDetachedCleanupOutboxGAgent.BuildRecordId(request.ActorId, request.CommandId),
                ActorId = request.ActorId,
                WorkflowName = request.WorkflowName,
                CommandId = request.CommandId,
                CreatedActorIds = { request.CreatedActorIds ?? [] },
                EnqueuedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            },
            request.CommandId);
        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }

    public async Task DiscardAsync(
        WorkflowRunDetachedCleanupDiscardRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = await ResolveOutboxActorAsync(ct);
        var envelope = CreateEnvelope(
            new WorkflowRunDetachedCleanupDiscardedEvent
            {
                RecordId = WorkflowRunDetachedCleanupOutboxGAgent.BuildRecordId(request.ActorId, request.CommandId),
                DiscardedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            },
            request.CommandId);
        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }

    public async Task TriggerReplayAsync(
        int batchSize,
        CancellationToken ct = default)
    {
        var actor = await ResolveOutboxActorAsync(ct);
        var envelope = CreateEnvelope(
            new WorkflowRunDetachedCleanupTriggerReplayEvent
            {
                BatchSize = batchSize,
            },
            correlationId: "replay");
        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }

    private async Task<IActor> ResolveOutboxActorAsync(CancellationToken ct)
    {
        var actorId = WorkflowRunDetachedCleanupOutboxGAgent.BuildActorId(DefaultScopeId);
        var existing = await _runtime.GetAsync(actorId);
        if (existing != null)
            return await EnsureActorTypeAsync(existing, actorId, ct);

        try
        {
            var created = await _runtime.CreateAsync<WorkflowRunDetachedCleanupOutboxGAgent>(actorId, ct);
            return await EnsureActorTypeAsync(created, actorId, ct);
        }
        catch (InvalidOperationException)
        {
            var raced = await _runtime.GetAsync(actorId);
            if (raced != null)
                return await EnsureActorTypeAsync(raced, actorId, ct);

            throw;
        }
    }

    private async Task<IActor> EnsureActorTypeAsync(IActor actor, string actorId, CancellationToken ct)
    {
        if (await _agentTypeVerifier.IsExpectedAsync(
                actorId,
                typeof(WorkflowRunDetachedCleanupOutboxGAgent),
                ct))
        {
            return actor;
        }

        throw new InvalidOperationException(
            $"Actor '{actorId}' is not a workflow detached cleanup outbox actor.");
    }

    private static EventEnvelope CreateEnvelope(IMessage payload, string correlationId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(OutboxPublisherId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId,
            },
        };
}
