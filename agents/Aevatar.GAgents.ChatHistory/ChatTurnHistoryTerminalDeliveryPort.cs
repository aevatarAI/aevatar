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
    private readonly IChatConversationContinuationAdmissionReader _continuationAdmissionReader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChatTurnHistoryTerminalDeliveryPort> _logger;

    public ChatTurnHistoryTerminalDeliveryPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        IChatConversationContinuationAdmissionReader continuationAdmissionReader,
        ILogger<ChatTurnHistoryTerminalDeliveryPort> logger,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _continuationAdmissionReader = continuationAdmissionReader
            ?? throw new ArgumentNullException(nameof(continuationAdmissionReader));
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
        var workflowCommandId = request.WorkflowCommandId.Trim();
        var conversationResolution = await ResolveConversationAsync(
                scopeId,
                workflowCommandId,
                request.Conversation,
                ct)
            .ConfigureAwait(false);
        if (conversationResolution.Failure != WorkflowChatHistoryTerminalDeliveryReservationFailure.None)
            return new WorkflowChatHistoryTerminalDeliveryReservationResult(null, null, null, conversationResolution.Failure);

        var turnId = conversationResolution.CreateConversationIfMissing
            ? ChatHistoryActorIds.CreateTurnId(scopeId, workflowCommandId)
            : CreateIdentity();
        var deliveryActorId = ChatTurnHistoryDeliveryActorIds.FromDeliveryId(deliveryId);
        var deliveryActorExists = await _actorRuntime.ExistsAsync(deliveryActorId).ConfigureAwait(false);
        if (!deliveryActorExists)
            await _actorRuntime.CreateAsync<ChatTurnHistoryDeliveryGAgent>(deliveryActorId, ct).ConfigureAwait(false);

        var command = new ChatTurnHistoryDeliveryReserveRequested
        {
            DeliveryId = deliveryId,
            ScopeId = scopeId,
            ConversationId = conversationResolution.ConversationId,
            TurnId = turnId,
            UserText = request.UserText.Trim(),
            SourceActorId = request.WorkflowActorId.Trim(),
            SourceCommandId = workflowCommandId,
            SourceCorrelationId = request.WorkflowCorrelationId,
            CreateConversationIfMissing = conversationResolution.CreateConversationIfMissing,
            RequestFingerprint = request.RequestFingerprint?.Trim() ?? string.Empty,
            ExposeCreateRecovery = conversationResolution.CreateConversationIfMissing,
        };
        await DispatchAsync(deliveryActorId, command, request.WorkflowCorrelationId, $"chat-history-delivery-reserve:{deliveryActorId}", ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Reserved chat history terminal delivery: deliveryActorId={DeliveryActorId} workflowActorId={WorkflowActorId} commandId={CommandId}",
            deliveryActorId,
            request.WorkflowActorId,
            workflowCommandId);

        var reservation = new WorkflowChatHistoryTerminalDeliveryReservation(
            deliveryActorId,
            deliveryId,
            request.WorkflowActorId.Trim(),
            workflowCommandId,
            ExistingReservation: deliveryActorExists);
        return WorkflowChatHistoryTerminalDeliveryReservationResult.Success(
            reservation,
            new WorkflowChatContext(
                scopeId,
                conversationResolution.ConversationId,
                turnId,
                conversationResolution.ConversationContext?.StateVersion ?? 0),
            conversationResolution.ConversationContext);
    }

    private async Task<ConversationIdentityResolution> ResolveConversationAsync(
        string scopeId,
        string workflowCommandId,
        WorkflowChatConversationIntent conversation,
        CancellationToken ct)
    {
        return conversation.Intent switch
        {
            WorkflowChatConversationIntentKind.Create =>
                ConversationIdentityResolution.Create(ChatHistoryActorIds.CreateConversationId(scopeId, workflowCommandId)),
            WorkflowChatConversationIntentKind.Continue => await ResolveExistingConversationAsync(
                    scopeId,
                    conversation.ConversationId,
                    conversation.MinimumStateVersion,
                    ct)
                .ConfigureAwait(false),
            _ => ConversationIdentityResolution.Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure.Unavailable),
        };
    }

    private async Task<ConversationIdentityResolution> ResolveExistingConversationAsync(
        string scopeId,
        string? conversationId,
        long? minimumStateVersion,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return ConversationIdentityResolution.Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure.ConversationNotFound);
        if (minimumStateVersion is not > 0)
            return ConversationIdentityResolution.Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure.Unavailable);

        var normalizedConversationId = conversationId.Trim();
        var admission = await _continuationAdmissionReader.GetContinuationAsync(
                scopeId,
                normalizedConversationId,
                minimumStateVersion.Value,
                ct)
            .ConfigureAwait(false);
        if (admission.CanContinue && admission.ConversationContext != null)
            return ConversationIdentityResolution.Continue(normalizedConversationId, admission.ConversationContext);

        return ConversationIdentityResolution.Failed(
            admission.Failure == ChatConversationContinuationAdmissionFailure.ReadModelNotReady
                ? WorkflowChatHistoryTerminalDeliveryReservationFailure.Unavailable
                : WorkflowChatHistoryTerminalDeliveryReservationFailure.ConversationNotFound);
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
            SourceActorId = receipt.ActorId,
            SourceCommandId = receipt.CommandId,
            SourceCorrelationId = receipt.CorrelationId,
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
        envelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId = operationId;

        var admission = await _dispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);
        if (!admission.Accepted)
            throw new InvalidOperationException("Chat history terminal delivery command was not accepted by the actor dispatch port.");
    }

    private static string CreateIdentity() => Guid.NewGuid().ToString("N");

    private readonly record struct ConversationIdentityResolution(
        string ConversationId,
        WorkflowConversationExecutionContext? ConversationContext,
        bool CreateConversationIfMissing,
        WorkflowChatHistoryTerminalDeliveryReservationFailure Failure)
    {
        public static ConversationIdentityResolution Create(string conversationId) =>
            new(conversationId, null, true, WorkflowChatHistoryTerminalDeliveryReservationFailure.None);

        public static ConversationIdentityResolution Continue(
            string conversationId,
            WorkflowConversationExecutionContext conversationContext) =>
            new(conversationId, conversationContext, false, WorkflowChatHistoryTerminalDeliveryReservationFailure.None);

        public static ConversationIdentityResolution Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure failure) =>
            new(string.Empty, null, false, failure);
    }
}
