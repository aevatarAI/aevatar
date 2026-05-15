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
    // Must match AgentRunGAgent.MaxRunRequestAgeMs so the dispatcher rejects
    // freshness violations at the boundary rather than letting them propagate
    // to the run actor inbox (where they would just be dropped). See ADR-0021.
    private const long MaxRequestAgeMs = 5L * 60_000L;

    private readonly IActorRuntime _actorRuntime;
    private readonly IStreamProvider _streamProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentRunDispatcher> _logger;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);

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

    public async Task<DispatchOutcome> DispatchAsync(NeedsLlmReplyEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CorrelationId))
            throw new InvalidOperationException("Deferred LLM reply request requires correlation_id for AgentRunGAgent dispatch.");

        var runId = request.CorrelationId.Trim();
        var actorId = AgentRunGAgent.BuildActorId(runId);
        await _dispatchGate.WaitAsync(ct);
        try
        {
            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

            if (request.RequestedAtUnixMs > 0 && nowMs - request.RequestedAtUnixMs > MaxRequestAgeMs)
            {
                _logger.LogWarning(
                    "Rejected stale deferred LLM reply run at dispatcher: runId={RunId} ageMs={AgeMs} thresholdMs={Threshold} target={TargetActorId}",
                    runId,
                    nowMs - request.RequestedAtUnixMs,
                    MaxRequestAgeMs,
                    request.TargetActorId);
                return new DispatchOutcome(
                    Phase: DispatchPhase.RejectedStale,
                    CommandId: string.Empty,
                    RunActorId: null,
                    AcceptedAtUnixMs: 0);
            }

            if (await _actorRuntime.ExistsAsync(actorId))
            {
                _logger.LogInformation(
                    "Rejected duplicate deferred LLM reply run at dispatcher: runId={RunId} actorId={ActorId} target={TargetActorId}",
                    runId,
                    actorId,
                    request.TargetActorId);
                return new DispatchOutcome(
                    Phase: DispatchPhase.RejectedDuplicate,
                    CommandId: string.Empty,
                    RunActorId: actorId,
                    AcceptedAtUnixMs: 0);
            }

            var actor = await _actorRuntime.CreateAsync<AgentRunGAgent>(actorId, ct);

            var commandId = BuildStartCommandId(runId);
            var command = new AgentRunStartRequested
            {
                Request = request.Clone(),
            };
            var envelope = new EventEnvelope
            {
                Id = commandId,
                Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                Payload = Any.Pack(command),
                Route = EnvelopeRouteSemantics.CreateDirect("channel-llm-reply-run-dispatcher", actor.Id),
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = runId,
                },
                Runtime = new EnvelopeRuntime
                {
                    Deduplication = new DeliveryDeduplication
                    {
                        OperationId = commandId,
                    },
                },
            };

            try
            {
                await _streamProvider.GetStream(actor.Id).ProduceAsync(envelope, ct);
            }
            catch
            {
                await DestroyCreatedActorAfterDispatchFailureAsync(actor.Id);
                throw;
            }

            _logger.LogInformation(
                "Accepted deferred LLM reply run for actor inbox: runId={RunId} actorId={ActorId} commandId={CommandId} target={TargetActorId}",
                runId,
                actor.Id,
                commandId,
                request.TargetActorId);
            return new DispatchOutcome(
                Phase: DispatchPhase.Accepted,
                CommandId: commandId,
                RunActorId: actor.Id,
                AcceptedAtUnixMs: nowMs);
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private static string BuildStartCommandId(string runId) => $"agent-run-start:{runId}";

    private async Task DestroyCreatedActorAfterDispatchFailureAsync(string actorId)
    {
        try
        {
            await _actorRuntime.DestroyAsync(actorId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to destroy agent run actor after dispatch enqueue failed: actorId={ActorId}",
                actorId);
        }
    }
}
