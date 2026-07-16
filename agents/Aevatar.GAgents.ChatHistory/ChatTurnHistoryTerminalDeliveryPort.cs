using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChatHistory;

public sealed class ChatTurnHistoryTerminalDeliveryPort : IWorkflowChatHistoryTerminalDeliveryPort
{
    private const string PublisherActorId = "chat-history-terminal-delivery-port";
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChatTurnHistoryTerminalDeliveryPort> _logger;

    public ChatTurnHistoryTerminalDeliveryPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        ILogger<ChatTurnHistoryTerminalDeliveryPort> logger,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkflowChatHistoryTerminalDeliveryReservation?> ReserveAsync(
        WorkflowChatHistoryTerminalDeliveryReservationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DeliveryId))
            return null;

        var deliveryId = request.DeliveryId.Trim();
        var deliveryActorId = ChatTurnHistoryDeliveryActorIds.FromDeliveryId(deliveryId);
        if (!await _actorRuntime.ExistsAsync(deliveryActorId).ConfigureAwait(false))
            await _actorRuntime.CreateAsync<ChatTurnHistoryDeliveryGAgent>(deliveryActorId, ct).ConfigureAwait(false);

        var command = new ChatTurnHistoryDeliveryReserveRequested
        {
            DeliveryId = deliveryId,
            ScopeId = request.ScopeId,
            ConversationId = request.ConversationId,
            TurnId = request.TurnId,
            UserText = request.UserText,
            WorkflowActorId = request.WorkflowActorId,
            WorkflowCommandId = request.WorkflowCommandId,
            WorkflowCorrelationId = request.WorkflowCorrelationId,
        };
        await DispatchAsync(deliveryActorId, command, request.WorkflowCorrelationId, $"chat-history-delivery-reserve:{deliveryActorId}", ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Reserved chat history terminal delivery: deliveryActorId={DeliveryActorId} workflowActorId={WorkflowActorId} commandId={CommandId}",
            deliveryActorId,
            request.WorkflowActorId,
            request.WorkflowCommandId);

        return new WorkflowChatHistoryTerminalDeliveryReservation(
            deliveryActorId,
            deliveryId,
            request.WorkflowActorId,
            request.WorkflowCommandId);
    }

    public async Task BindAcceptedAsync(
        WorkflowChatHistoryTerminalDeliveryReservation reservation,
        WorkflowChatRunAcceptedReceipt receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(receipt);

        var command = new ChatTurnHistoryDeliveryAcceptedBound
        {
            DeliveryId = reservation.DeliveryId,
            WorkflowActorId = receipt.ActorId,
            WorkflowCommandId = receipt.CommandId,
            WorkflowCorrelationId = receipt.CorrelationId,
        };
        await DispatchAsync(reservation.DeliveryActorId, command, receipt.CorrelationId, $"chat-history-delivery-bind:{reservation.DeliveryActorId}", ct)
            .ConfigureAwait(false);
    }

    public async Task AbandonAsync(
        WorkflowChatHistoryTerminalDeliveryReservation reservation,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        var command = new ChatTurnHistoryDeliveryAbandonedEvent
        {
            DeliveryId = reservation.DeliveryId,
            Reason = string.IsNullOrWhiteSpace(reason)
                ? "workflow_dispatch_not_accepted"
                : reason.Trim(),
        };
        await DispatchAsync(reservation.DeliveryActorId, command, reservation.WorkflowCommandId, $"chat-history-delivery-abandon:{reservation.DeliveryActorId}", ct)
            .ConfigureAwait(false);
    }

    private async Task DispatchAsync(
        string actorId,
        IMessage command,
        string? correlationId,
        string operationId,
        CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = string.IsNullOrWhiteSpace(correlationId)
                    ? actorId
                    : correlationId.Trim(),
            },
        };
        envelope.EnsureRuntime().EnsureDeduplication().OperationId = operationId;

        var admission = await _dispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);
        if (!admission.Accepted)
            throw new InvalidOperationException("Chat history terminal delivery command was not accepted by the actor dispatch port.");
    }
}
