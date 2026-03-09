using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class ActorProjectionDispatchCompensationOutbox : IProjectionDispatchCompensationOutbox
{
    private const string OutboxPublisherId = "projection.compensation.outbox";
    private const string DefaultScopeId = "workflow";
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAgentTypeVerifier _agentTypeVerifier;

    public ActorProjectionDispatchCompensationOutbox(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IAgentTypeVerifier agentTypeVerifier)
    {
        _runtime = runtime;
        _dispatchPort = dispatchPort;
        _agentTypeVerifier = agentTypeVerifier;
    }

    public async Task EnqueueAsync(
        ProjectionCompensationEnqueuedEvent evt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var actor = await ResolveOutboxActorAsync(ct);
        var envelope = CreateEnvelope(evt, evt.RecordId);
        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }

    public async Task TriggerReplayAsync(
        int batchSize,
        CancellationToken ct = default)
    {
        var actor = await ResolveOutboxActorAsync(ct);
        var envelope = CreateEnvelope(
            new ProjectionCompensationTriggerReplayEvent { BatchSize = batchSize },
            correlationId: "replay");
        await _dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }

    private async Task<IActor> ResolveOutboxActorAsync(CancellationToken ct)
    {
        var actorId = WorkflowProjectionDispatchCompensationOutboxGAgent.BuildActorId(DefaultScopeId);
        var existing = await _runtime.GetAsync(actorId);
        if (existing != null)
            return await EnsureActorTypeAsync(existing, actorId, ct);

        try
        {
            var created = await _runtime.CreateAsync<WorkflowProjectionDispatchCompensationOutboxGAgent>(actorId, ct);
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
                typeof(WorkflowProjectionDispatchCompensationOutboxGAgent),
                ct))
            return actor;

        throw new InvalidOperationException(
            $"Actor '{actorId}' is not a projection dispatch compensation outbox actor.");
    }

    private static EventEnvelope CreateEnvelope(IMessage payload, string correlationId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            PublisherId = OutboxPublisherId,
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
        };
}
