using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Thin Channel.Runtime port implementation that creates the run actor and
/// dispatches the start command. It holds no run state.
/// </summary>
public sealed class AgentRunDispatcher : IChannelLlmReplyRunDispatcher
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentRunDispatcher> _logger;

    public AgentRunDispatcher(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        ILogger<AgentRunDispatcher> logger,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task DispatchAsync(NeedsLlmReplyEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Refactor (iter98/cluster-002): Old=missing run_id could fall back to correlation_id; New=run_id is required and correlation_id is trace-only.
        var runId = AgentRunId.Parse(request.RunId, nameof(request.RunId));
        var actorId = AgentRunActorIds.ForRun(runId);

        var actor = await _actorRuntime.CreateAsync<AgentRunGAgent>(actorId, ct);

        var commandId = BuildStartCommandId(runId);
        var command = new AgentRunStartRequested
        {
            Request = request.Clone(),
            RunId = runId.Value,
        };
        command.Request.RunId = runId.Value;
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect("channel-llm-reply-run-dispatcher", actor.Id),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? runId.Value
                    : request.CorrelationId.Trim(),
            },
            Runtime = new EnvelopeRuntime
            {
                DeliveryIdentity = new DeliveryIdentity
                {
                    OperationId = commandId,
                },
            },
        };

        // Refactor (iter56/cluster-935-agent-run-actor-admission): old=dispatcher in-process admission, new=actor-owned admission with plain Task
        //   This adapter only hands the start envelope to IActorDispatchPort.
        //   AgentRunGAgent owns stale/duplicate admission after inbox delivery.
        await _actorDispatchPort.DispatchAsync(actor.Id, envelope, ct);

        _logger.LogInformation(
            "Accepted deferred LLM reply run for actor dispatch: runId={RunId} actorId={ActorId} commandId={CommandId} target={TargetActorId}",
            runId.Value,
            actor.Id,
            commandId,
            request.TargetActorId);
    }

    private static string BuildStartCommandId(AgentRunId runId) => $"agent-run-start:{runId.Value}";
}
