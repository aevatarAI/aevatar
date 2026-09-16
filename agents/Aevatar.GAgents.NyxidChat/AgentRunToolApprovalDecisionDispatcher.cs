using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public interface IAgentRunToolApprovalDecisionDispatcher
{
    Task DispatchAsync(AgentRunToolApprovalDecisionRequested command, CancellationToken ct);
}

/// <summary>
/// Routes a typed approval callback to the exact run actor. It owns no approval
/// state; actor rehydration and identity validation stay inside AgentRunGAgent.
/// </summary>
public sealed class AgentRunToolApprovalDecisionDispatcher : IAgentRunToolApprovalDecisionDispatcher
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentRunToolApprovalDecisionDispatcher> _logger;

    public AgentRunToolApprovalDecisionDispatcher(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        ILogger<AgentRunToolApprovalDecisionDispatcher> logger,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task DispatchAsync(AgentRunToolApprovalDecisionRequested command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var runId = AgentRunId.Parse(command.RunId, nameof(command.RunId));
        var actorId = AgentRunActorIds.ForRun(runId);
        var actor = await _actorRuntime.CreateAsync<AgentRunGAgent>(actorId, ct);
        var commandId = BuildCommandId(command);
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect("agent-run-tool-approval-dispatcher", actor.Id),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = command.Request?.CorrelationId ?? command.ApprovalRequestId,
            },
            Runtime = new EnvelopeRuntime
            {
                DeliveryIdentity = new DeliveryIdentity
                {
                    OperationId = commandId,
                },
            },
        };

        await _actorDispatchPort.DispatchAsync(actor.Id, envelope, ct);
        _logger.LogInformation(
            "Accepted AgentRun tool approval decision for actor dispatch: runId={RunId} actorId={ActorId} approvalRequest={ApprovalRequestId} approved={Approved}",
            runId.Value,
            actor.Id,
            command.ApprovalRequestId,
            command.Approved);
    }

    private static string BuildCommandId(AgentRunToolApprovalDecisionRequested command) =>
        $"agent-run-tool-approval:{command.RunId}:{command.ApprovalRequestId}:{(command.Approved ? "approve" : "reject")}";
}
