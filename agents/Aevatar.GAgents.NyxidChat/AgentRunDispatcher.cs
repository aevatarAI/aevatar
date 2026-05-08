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
    private readonly IStreamProvider _streamProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentRunDispatcher> _logger;

    public AgentRunDispatcher(
        IActorRuntime actorRuntime,
        IStreamProvider streamProvider,
        ILogger<AgentRunDispatcher> logger,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task DispatchAsync(NeedsLlmReplyEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CorrelationId))
            throw new InvalidOperationException("Deferred LLM reply request requires correlation_id for AgentRunGAgent dispatch.");

        var runId = request.CorrelationId.Trim();
        var actorId = AgentRunGAgent.BuildActorId(runId);
        var actor = await _actorRuntime.GetAsync(actorId)
                    ?? await _actorRuntime.CreateAsync<AgentRunGAgent>(actorId, ct);

        var command = new AgentRunStartRequested
        {
            Request = request.Clone(),
        };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect("channel-llm-reply-run-dispatcher", actor.Id),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = runId,
            },
        };

        await _streamProvider.GetStream(actor.Id).ProduceAsync(envelope, ct);
        _logger.LogInformation(
            "Accepted deferred LLM reply run for actor inbox: runId={RunId} actorId={ActorId} target={TargetActorId}",
            runId,
            actor.Id,
            request.TargetActorId);
    }
}
