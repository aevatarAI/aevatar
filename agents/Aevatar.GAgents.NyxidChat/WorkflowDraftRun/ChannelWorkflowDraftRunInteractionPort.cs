using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

public sealed class ChannelWorkflowDraftRunInteractionPort : IChannelWorkflowDraftRunInteractionPort
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChannelWorkflowDraftRunInteractionPort> _logger;

    public ChannelWorkflowDraftRunInteractionPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        ILogger<ChannelWorkflowDraftRunInteractionPort> logger,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task DispatchAsync(NeedsWorkflowDraftRunEvent request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runId = ChannelWorkflowDraftRunId.Parse(request.RunId, nameof(request.RunId));
        var actorId = ChannelWorkflowDraftRunActorIds.ForRun(runId);
        var actor = await _actorRuntime.CreateAsync<ChannelWorkflowDraftRunGAgent>(actorId, ct).ConfigureAwait(false);

        var commandId = BuildStartCommandId(runId);
        var command = new ChannelWorkflowDraftRunStartRequested
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
            Route = EnvelopeRouteSemantics.CreateDirect("channel-workflow-draft-run-dispatcher", actor.Id),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? runId.Value
                    : request.CorrelationId.Trim(),
            },
            Runtime = new EnvelopeRuntime
            {
                Deduplication = new DeliveryDeduplication
                {
                    OperationId = commandId,
                },
            },
        };

        await _actorDispatchPort.DispatchAsync(actor.Id, envelope, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Accepted workflow draft run for actor dispatch: runId={RunId} actorId={ActorId} commandId={CommandId} target={TargetActorId}",
            runId.Value,
            actor.Id,
            commandId,
            request.TargetActorId);
    }

    private static string BuildStartCommandId(ChannelWorkflowDraftRunId runId) =>
        $"workflow-draft-run-start:{runId.Value}";
}
