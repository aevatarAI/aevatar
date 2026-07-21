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
    private readonly IChatCreateRecoveryReader? _createRecoveryReader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChatTurnHistoryTerminalDeliveryPort> _logger;

    public ChatTurnHistoryTerminalDeliveryPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        IChatConversationContinuationAdmissionReader continuationAdmissionReader,
        ILogger<ChatTurnHistoryTerminalDeliveryPort> logger,
        IChatCreateRecoveryReader? createRecoveryReader = null,
        TimeProvider? timeProvider = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _continuationAdmissionReader = continuationAdmissionReader
            ?? throw new ArgumentNullException(nameof(continuationAdmissionReader));
        _createRecoveryReader = createRecoveryReader;
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

        var scopeId = request.ScopeId.Trim();
        var userText = request.UserText.Trim();
        var workflowActorId = request.WorkflowActorId.Trim();
        var workflowCommandId = request.WorkflowCommandId.Trim();
        var requestedDeliveryId = request.DeliveryId.Trim();
        var conversationResolution = await ResolveConversationAsync(
                scopeId,
                userText,
                request.Conversation,
                ct)
            .ConfigureAwait(false);
        if (conversationResolution.Failure != WorkflowChatHistoryTerminalDeliveryReservationFailure.None)
            return new WorkflowChatHistoryTerminalDeliveryReservationResult(null, null, conversationResolution.Failure);

        if (conversationResolution.RecoveryRecord is not null)
        {
            var recoveredReservation = new WorkflowChatHistoryTerminalDeliveryReservation(
                conversationResolution.RecoveryRecord.DeliveryActorId,
                requestedDeliveryId,
                workflowActorId,
                workflowCommandId);
            return WorkflowChatHistoryTerminalDeliveryReservationResult.Success(
                recoveredReservation,
                new WorkflowChatContext(
                    scopeId,
                    conversationResolution.RecoveryRecord.ConversationId,
                    conversationResolution.RecoveryRecord.TurnId),
                replayed: true);
        }

        var deliveryId = string.IsNullOrWhiteSpace(conversationResolution.DeliveryId)
            ? requestedDeliveryId
            : conversationResolution.DeliveryId;
        var turnId = string.IsNullOrWhiteSpace(conversationResolution.TurnId)
            ? CreateIdentity()
            : conversationResolution.TurnId;
        var deliveryActorId = ChatTurnHistoryDeliveryActorIds.FromDeliveryId(deliveryId);
        if (!string.IsNullOrWhiteSpace(conversationResolution.CreateIdempotencyKey) &&
            await _actorRuntime.ExistsAsync(deliveryActorId).ConfigureAwait(false))
        {
            var replayReservation = new WorkflowChatHistoryTerminalDeliveryReservation(
                deliveryActorId,
                deliveryId,
                workflowActorId,
                workflowCommandId);
            return WorkflowChatHistoryTerminalDeliveryReservationResult.Success(
                replayReservation,
                new WorkflowChatContext(scopeId, conversationResolution.ConversationId, turnId),
                replayed: true);
        }

        if (!await _actorRuntime.ExistsAsync(deliveryActorId).ConfigureAwait(false))
            await _actorRuntime.CreateAsync<ChatTurnHistoryDeliveryGAgent>(deliveryActorId, ct).ConfigureAwait(false);

        var command = new ChatTurnHistoryDeliveryReserveRequested
        {
            DeliveryId = deliveryId,
            ScopeId = scopeId,
            ConversationId = conversationResolution.ConversationId,
            TurnId = turnId,
            UserText = userText,
            WorkflowActorId = workflowActorId,
            WorkflowCommandId = workflowCommandId,
            WorkflowCorrelationId = request.WorkflowCorrelationId,
            CreateConversationIfMissing = conversationResolution.CreateConversationIfMissing,
            CreateIdempotencyKey = conversationResolution.CreateIdempotencyKey,
            CreateRequestHash = conversationResolution.CreateRequestHash,
        };
        await DispatchAsync(deliveryActorId, command, request.WorkflowCorrelationId, $"chat-history-delivery-reserve:{deliveryActorId}", ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Reserved chat history terminal delivery: deliveryActorId={DeliveryActorId} workflowActorId={WorkflowActorId} commandId={CommandId}",
            deliveryActorId,
            workflowActorId,
            workflowCommandId);

        var reservation = new WorkflowChatHistoryTerminalDeliveryReservation(
            deliveryActorId,
            deliveryId,
            workflowActorId,
            workflowCommandId);
        return WorkflowChatHistoryTerminalDeliveryReservationResult.Success(
            reservation,
            new WorkflowChatContext(scopeId, conversationResolution.ConversationId, turnId));
    }

    private async Task<ConversationIdentityResolution> ResolveConversationAsync(
        string scopeId,
        string userText,
        WorkflowChatConversationIntent conversation,
        CancellationToken ct)
    {
        return conversation.Intent switch
        {
            WorkflowChatConversationIntentKind.Create => await ResolveCreateConversationAsync(
                    scopeId,
                    userText,
                    conversation.CreateIdempotency,
                    ct)
                .ConfigureAwait(false),
            WorkflowChatConversationIntentKind.Continue => await ResolveExistingConversationAsync(scopeId, conversation.ConversationId, ct)
                .ConfigureAwait(false),
            _ => ConversationIdentityResolution.Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure.Unavailable),
        };
    }

    private async Task<ConversationIdentityResolution> ResolveCreateConversationAsync(
        string scopeId,
        string userText,
        WorkflowChatCreateIdempotencyIdentity? createIdempotency,
        CancellationToken ct)
    {
        if (createIdempotency is null)
            return ConversationIdentityResolution.Create(CreateIdentity());

        var createIdempotencyKey = createIdempotency.CreateIdempotencyKey;
        var requestHash = createIdempotency.BuildRequestHash(scopeId, userText);
        var recoveryRecord = _createRecoveryReader is null
            ? null
            : await _createRecoveryReader.FindAsync(scopeId, createIdempotencyKey, ct).ConfigureAwait(false);
        if (recoveryRecord is not null)
        {
            return string.Equals(recoveryRecord.ScopeId, scopeId, StringComparison.Ordinal) &&
                   string.Equals(recoveryRecord.CreateIdempotencyKey, createIdempotencyKey, StringComparison.Ordinal) &&
                   string.Equals(recoveryRecord.RequestHash, requestHash, StringComparison.Ordinal)
                ? ConversationIdentityResolution.Replay(recoveryRecord)
                : ConversationIdentityResolution.Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure.IdempotencyConflict);
        }

        return ConversationIdentityResolution.Create(
            ChatHistoryActorIds.CreateConversationId(scopeId, createIdempotencyKey),
            ChatHistoryActorIds.CreateTurnId(scopeId, createIdempotencyKey),
            ChatHistoryActorIds.CreateDeliveryId(scopeId, createIdempotencyKey),
            createIdempotencyKey,
            requestHash);
    }

    private async Task<ConversationIdentityResolution> ResolveExistingConversationAsync(
        string scopeId,
        string? conversationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return ConversationIdentityResolution.Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure.ConversationNotFound);

        var normalizedConversationId = conversationId.Trim();
        return await _continuationAdmissionReader.CanContinueAsync(scopeId, normalizedConversationId, ct).ConfigureAwait(false)
            ? ConversationIdentityResolution.Continue(normalizedConversationId)
            : ConversationIdentityResolution.Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure.ConversationNotFound);
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
        string TurnId,
        string DeliveryId,
        string CreateIdempotencyKey,
        string CreateRequestHash,
        ChatCreateRecoveryRecord? RecoveryRecord,
        bool CreateConversationIfMissing,
        WorkflowChatHistoryTerminalDeliveryReservationFailure Failure)
    {
        public static ConversationIdentityResolution Create(
            string conversationId,
            string turnId = "",
            string deliveryId = "",
            string createIdempotencyKey = "",
            string createRequestHash = "") =>
            new(
                conversationId,
                turnId,
                deliveryId,
                createIdempotencyKey,
                createRequestHash,
                null,
                true,
                WorkflowChatHistoryTerminalDeliveryReservationFailure.None);

        public static ConversationIdentityResolution Continue(string conversationId) =>
            new(
                conversationId,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                false,
                WorkflowChatHistoryTerminalDeliveryReservationFailure.None);

        public static ConversationIdentityResolution Replay(ChatCreateRecoveryRecord recoveryRecord) =>
            new(
                recoveryRecord.ConversationId,
                recoveryRecord.TurnId,
                string.Empty,
                recoveryRecord.CreateIdempotencyKey,
                recoveryRecord.RequestHash,
                recoveryRecord,
                true,
                WorkflowChatHistoryTerminalDeliveryReservationFailure.None);

        public static ConversationIdentityResolution Failed(WorkflowChatHistoryTerminalDeliveryReservationFailure failure) =>
            new(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                false,
                failure);
    }
}
