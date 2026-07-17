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

    public async Task<WorkflowChatHistoryTerminalDeliveryReservationResult> ReserveAsync(
        WorkflowChatHistoryTerminalDeliveryReservationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DeliveryId) ||
            string.IsNullOrWhiteSpace(request.ScopeId) ||
            string.IsNullOrWhiteSpace(request.UserText) ||
            string.IsNullOrWhiteSpace(request.WorkflowActorId) ||
            string.IsNullOrWhiteSpace(request.WorkflowCommandId) ||
            request.Conversation is null)
        {
            return WorkflowChatHistoryTerminalDeliveryReservationResult.Unavailable();
        }

        var deliveryId = request.DeliveryId.Trim();
        var scopeId = request.ScopeId.Trim();
        var conversationResolution = await ResolveConversationAsync(scopeId, request.Conversation).ConfigureAwait(false);
        if (conversationResolution.Failure != WorkflowChatHistoryTerminalDeliveryReservationFailure.None)
            return new WorkflowChatHistoryTerminalDeliveryReservationResult(null, null, conversationResolution.Failure);

        var turnId = CreateIdentity();
        var deliveryActorId = ChatTurnHistoryDeliveryActorIds.FromDeliveryId(deliveryId);
        if (!await _actorRuntime.ExistsAsync(deliveryActorId).ConfigureAwait(false))
            await _actorRuntime.CreateAsync<ChatTurnHistoryDeliveryGAgent>(deliveryActorId, ct).ConfigureAwait(false);

        var command = new ChatTurnHistoryDeliveryReserveRequested
        {
            DeliveryId = deliveryId,
            ScopeId = scopeId,
            ConversationId = conversationResolution.ConversationId,
            TurnId = turnId,
            UserText = request.UserText.Trim(),
            WorkflowActorId = request.WorkflowActorId.Trim(),
            WorkflowCommandId = request.WorkflowCommandId.Trim(),
            WorkflowCorrelationId = request.WorkflowCorrelationId,
            CreateConversationIfMissing = conversationResolution.CreateConversationIfMissing,
        };
        await DispatchAsync(deliveryActorId, command, request.WorkflowCorrelationId, $"chat-history-delivery-reserve:{deliveryActorId}", ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Reserved chat history terminal delivery: deliveryActorId={DeliveryActorId} workflowActorId={WorkflowActorId} commandId={CommandId}",
            deliveryActorId,
            request.WorkflowActorId,
            request.WorkflowCommandId);

        var reservation = new WorkflowChatHistoryTerminalDeliveryReservation(
            deliveryActorId,
            deliveryId,
            request.WorkflowActorId.Trim(),
            request.WorkflowCommandId.Trim());
        return WorkflowChatHistoryTerminalDeliveryReservationResult.Success(
            reservation,
            new WorkflowChatContext(scopeId, conversationResolution.ConversationId, turnId));
    }

    private async Task<ConversationIdentityResolution> ResolveConversationAsync(
        string scopeId,
        WorkflowChatConversationIntent conversation)
    {
        return conversation.Intent switch
        {
            WorkflowChatConversationIntentKind.Create => ConversationIdentityResolution.Create(CreateIdentity()),
            WorkflowChatConversationIntentKind.Continue => await ResolveExistingConversationAsync(scopeId, conversation.ConversationId)
                .ConfigureAwait(false),
            _ => ConversationIdentityResolution.Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure.Unavailable),
        };
    }

    private async Task<ConversationIdentityResolution> ResolveExistingConversationAsync(
        string scopeId,
        string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return ConversationIdentityResolution.Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure.ConversationNotFound);

        var normalizedConversationId = conversationId.Trim();
        var actorId = ChatHistoryActorIds.Conversation(scopeId, normalizedConversationId);
        if (!await _actorRuntime.ExistsAsync(actorId).ConfigureAwait(false))
            return ConversationIdentityResolution.Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure.ConversationNotFound);

        return ConversationIdentityResolution.Continue(normalizedConversationId);
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

    private static string CreateIdentity() => Guid.NewGuid().ToString("N");

    private readonly record struct ConversationIdentityResolution(
        string ConversationId,
        bool CreateConversationIfMissing,
        WorkflowChatHistoryTerminalDeliveryReservationFailure Failure)
    {
        public static ConversationIdentityResolution Create(string conversationId) =>
            new(conversationId, true, WorkflowChatHistoryTerminalDeliveryReservationFailure.None);

        public static ConversationIdentityResolution Continue(string conversationId) =>
            new(conversationId, false, WorkflowChatHistoryTerminalDeliveryReservationFailure.None);

        public static ConversationIdentityResolution Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure failure) =>
            new(string.Empty, false, failure);
    }
}
