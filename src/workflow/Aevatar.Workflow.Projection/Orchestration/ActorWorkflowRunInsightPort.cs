using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class ActorWorkflowRunInsightPort
    : IWorkflowRunInsightActorPort
{
    private const string PublisherActorId = "workflow.run.insight.bridge";
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAgentTypeVerifier _agentTypeVerifier;
    private readonly ILogger<ActorWorkflowRunInsightPort> _logger;

    public ActorWorkflowRunInsightPort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IAgentTypeVerifier agentTypeVerifier,
        ILogger<ActorWorkflowRunInsightPort>? logger = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _agentTypeVerifier = agentTypeVerifier ?? throw new ArgumentNullException(nameof(agentTypeVerifier));
        _logger = logger ?? NullLogger<ActorWorkflowRunInsightPort>.Instance;
    }

    public async Task EnsureActorAsync(string rootActorId, CancellationToken ct = default)
    {
        _ = await ResolveActorAsync(rootActorId, ct);
    }

    public async Task PublishObservedAsync(
        string rootActorId,
        WorkflowRunInsightObservedEvent evt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var actor = await ResolveActorAsync(rootActorId, ct);
        await _dispatchPort.DispatchAsync(
            actor.Id,
            CreateEnvelope(actor.Id, evt, evt.CommandId, evt.SourceEventId),
            ct);
    }

    public async Task CaptureTopologyAsync(
        string rootActorId,
        string workflowName,
        string commandId,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        DateTimeOffset capturedAt,
        CancellationToken ct = default)
    {
        var actor = await ResolveActorAsync(rootActorId, ct);
        var evt = new WorkflowRunInsightTopologyCapturedEvent
        {
            RootActorId = rootActorId,
            WorkflowName = workflowName ?? string.Empty,
            CommandId = commandId ?? string.Empty,
            CapturedAtUtc = Timestamp.FromDateTimeOffset(capturedAt.ToUniversalTime()),
        };
        evt.TopologyEntries.Add(topology.Select(edge => new WorkflowRunInsightTopologyEdge
        {
            Parent = edge.Parent,
            Child = edge.Child,
        }));

        await _dispatchPort.DispatchAsync(
            actor.Id,
            CreateEnvelope(actor.Id, evt, commandId, "topology"),
            ct);
    }

    public async Task MarkStoppedAsync(
        string rootActorId,
        string reason,
        DateTimeOffset stoppedAt,
        CancellationToken ct = default)
    {
        var actor = await ResolveActorAsync(rootActorId, ct);
        var evt = new WorkflowRunInsightStoppedEvent
        {
            RootActorId = rootActorId,
            Reason = reason ?? string.Empty,
            StoppedAtUtc = Timestamp.FromDateTimeOffset(stoppedAt.ToUniversalTime()),
        };

        await _dispatchPort.DispatchAsync(
            actor.Id,
            CreateEnvelope(actor.Id, evt, correlationId: "stopped", causationEventId: "stopped"),
            ct);
    }

    private async Task<IActor> ResolveActorAsync(string rootActorId, CancellationToken ct)
    {
        var actorId = WorkflowRunInsightGAgent.BuildActorId(rootActorId);
        var existing = await _runtime.GetAsync(actorId);
        if (existing != null)
            return await EnsureActorTypeAsync(existing, actorId, ct);

        try
        {
            var created = await _runtime.CreateAsync<WorkflowRunInsightGAgent>(actorId, ct);
            return await EnsureActorTypeAsync(created, actorId, ct);
        }
        catch (InvalidOperationException)
        {
            var raced = await _runtime.GetAsync(actorId);
            if (raced != null)
                return await EnsureActorTypeAsync(raced, actorId, ct);

            _logger.LogWarning(
                "Workflow insight actor creation raced but no actor was available afterwards. actorId={ActorId}",
                actorId);
            throw;
        }
    }

    private async Task<IActor> EnsureActorTypeAsync(IActor actor, string actorId, CancellationToken ct)
    {
        if (await _agentTypeVerifier.IsExpectedAsync(actorId, typeof(WorkflowRunInsightGAgent), ct))
            return actor;

        throw new InvalidOperationException($"Actor '{actorId}' is not a workflow run insight actor.");
    }

    private static EventEnvelope CreateEnvelope(
        string targetActorId,
        IMessage payload,
        string? correlationId,
        string? causationEventId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId ?? string.Empty,
                CausationEventId = causationEventId ?? string.Empty,
            },
        };
}
