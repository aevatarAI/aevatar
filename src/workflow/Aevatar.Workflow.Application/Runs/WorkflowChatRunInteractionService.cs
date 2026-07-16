using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowChatRunInteractionService : IWorkflowChatRunInteractionPort
{
    private static readonly TimeSpan ChatHistoryTerminalDeliveryLifetime = TimeSpan.FromMinutes(30);
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowExecutionProjectionPort _projectionPort;
    private readonly IWorkflowRunProvisioningPort _runProvisioningPort;
    private readonly ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> _inner;
    private readonly WorkflowDirectFallbackPolicy _fallbackPolicy;
    private readonly IWorkflowChatHistoryTerminalDeliveryPort? _chatHistoryTerminalDeliveryPort;

    public WorkflowChatRunInteractionService(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowExecutionProjectionPort projectionPort,
        IWorkflowRunProvisioningPort runProvisioningPort,
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> inner,
        WorkflowDirectFallbackPolicy fallbackPolicy,
        IWorkflowChatHistoryTerminalDeliveryPort? chatHistoryTerminalDeliveryPort = null)
    {
        _actorResolver = actorResolver ?? throw new ArgumentNullException(nameof(actorResolver));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _runProvisioningPort = runProvisioningPort ?? throw new ArgumentNullException(nameof(runProvisioningPort));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _fallbackPolicy = fallbackPolicy ?? throw new ArgumentNullException(nameof(fallbackPolicy));
        _chatHistoryTerminalDeliveryPort = chatHistoryTerminalDeliveryPort;
    }

    public async Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var commandId = string.IsNullOrWhiteSpace(request.CommandIdSeed)
            ? CreateInteractionId()
            : request.CommandIdSeed.Trim();
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationIdSeed)
            ? CreateInteractionId()
            : request.CorrelationIdSeed.Trim();
        var currentRequest = request with
        {
            CommandIdSeed = commandId,
            CorrelationIdSeed = correlationId,
            TargetSeed = null,
        };

        while (true)
        {
            var attempt = await StartAttemptAsync(currentRequest, ct).ConfigureAwait(false);
            if (!attempt.Succeeded)
                return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>.Failure(attempt.Error);

            try
            {
                return await ExecuteAttemptAsync(
                    attempt.Value!,
                    emitAsync,
                    onAcceptedAsync,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!attempt.Value!.Accepted &&
                                       _fallbackPolicy.TryCreateFallbackCommand(currentRequest, ex, out var fallbackRequest))
            {
                await CleanupAttemptAsync(attempt.Value!, CancellationToken.None).ConfigureAwait(false);
                currentRequest = fallbackRequest with
                {
                    CommandIdSeed = commandId,
                    CorrelationIdSeed = correlationId,
                    Headers = request.Headers,
                    TargetSeed = null,
                };
            }
            catch
            {
                if (!attempt.Value!.Accepted)
                    await CleanupAttemptAsync(attempt.Value!, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task<AttemptStartResult> StartAttemptAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct)
    {
        if (!_projectionPort.ProjectionEnabled)
            return AttemptStartResult.Failure(WorkflowChatRunStartError.ProjectionDisabled);

        if (Aevatar.Workflow.Abstractions.WorkflowCallerCredentialTokens
            .ParseOptional(request.CallerCredential?.BearerToken)
            .IsInvalid)
            return AttemptStartResult.Failure(WorkflowChatRunStartError.InvalidCallerCredential);

        var actorResolution = await _actorResolver.ResolveOrCreateAsync(request, ct).ConfigureAwait(false);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Target == null)
            return AttemptStartResult.Failure(actorResolution.Error);

        var seededRequest = request with
        {
            TargetSeed = new WorkflowRunTargetSeed(
                actorResolution.Target.ActorId,
                actorResolution.WorkflowNameForRun,
                actorResolution.Target.CreatedActorIds,
                request.Source),
        };

        return AttemptStartResult.Success(new WorkflowChatRunInteractionAttempt(
            seededRequest,
            actorResolution.Target.CreatedActorIds));
    }

    private async Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAttemptAsync(
        WorkflowChatRunInteractionAttempt attempt,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
        CancellationToken ct)
    {
        var chatHistoryDelivery = await ReserveChatHistoryDeliveryAsync(attempt.Request, ct).ConfigureAwait(false);

        async ValueTask OnAcceptedAsync(WorkflowChatRunAcceptedReceipt receipt, CancellationToken token)
        {
            attempt.MarkAccepted();
            if (chatHistoryDelivery is not null)
                await _chatHistoryTerminalDeliveryPort!
                    .BindAcceptedAsync(chatHistoryDelivery, receipt, token)
                    .ConfigureAwait(false);
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, token).ConfigureAwait(false);
        }

        var dispatchRequest = chatHistoryDelivery is null
            ? attempt.Request
            : attempt.Request with
            {
                CompletionNotificationTarget = CreateCompletionNotificationTarget(chatHistoryDelivery),
            };
        CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus> result;
        try
        {
            result = await _inner.ExecuteAsync(
                dispatchRequest,
                emitAsync,
                OnAcceptedAsync,
                ct).ConfigureAwait(false);
        }
        catch
        {
            if (!attempt.Accepted)
                await CleanupAttemptAsync(attempt, CancellationToken.None, chatHistoryDelivery).ConfigureAwait(false);
            throw;
        }

        if (!result.Succeeded && !attempt.Accepted)
            await CleanupAttemptAsync(attempt, CancellationToken.None, chatHistoryDelivery).ConfigureAwait(false);

        return result;
    }

    private async Task<WorkflowChatHistoryTerminalDeliveryReservation?> ReserveChatHistoryDeliveryAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct)
    {
        if (_chatHistoryTerminalDeliveryPort is null ||
            request.ChatHistory is null ||
            request.TargetSeed is null ||
            string.IsNullOrWhiteSpace(request.ScopeId) ||
            string.IsNullOrWhiteSpace(request.ChatHistory.ConversationId) ||
            string.IsNullOrWhiteSpace(request.ChatHistory.TurnId) ||
            string.IsNullOrWhiteSpace(request.ChatHistory.UserText) ||
            string.IsNullOrWhiteSpace(request.CommandIdSeed))
        {
            return null;
        }

        var deliveryId = $"chat-history-delivery-{request.TargetSeed.ActorId.Trim()}-{request.CommandIdSeed.Trim()}";
        return await _chatHistoryTerminalDeliveryPort.ReserveAsync(
                new WorkflowChatHistoryTerminalDeliveryReservationRequest(
                    deliveryId,
                    request.ScopeId.Trim(),
                    request.ChatHistory.ConversationId.Trim(),
                    request.ChatHistory.TurnId.Trim(),
                    request.ChatHistory.UserText.Trim(),
                    request.TargetSeed.ActorId.Trim(),
                    request.CommandIdSeed.Trim(),
                    request.CorrelationIdSeed?.Trim() ?? string.Empty),
                ct)
            .ConfigureAwait(false);
    }

    private static Application.Abstractions.Runs.WorkflowCompletionNotificationTarget CreateCompletionNotificationTarget(
        WorkflowChatHistoryTerminalDeliveryReservation reservation) =>
        new(
            reservation.DeliveryActorId,
            reservation.DeliveryId,
            DateTimeOffset.UtcNow.Add(ChatHistoryTerminalDeliveryLifetime).ToUnixTimeMilliseconds());

    private async Task CleanupAttemptAsync(
        WorkflowChatRunInteractionAttempt attempt,
        CancellationToken ct,
        WorkflowChatHistoryTerminalDeliveryReservation? chatHistoryDelivery = null)
    {
        if (!attempt.TryMarkCleanedUp())
            return;

        if (chatHistoryDelivery is not null && _chatHistoryTerminalDeliveryPort is not null)
            await _chatHistoryTerminalDeliveryPort
                .AbandonAsync(chatHistoryDelivery, "workflow_dispatch_not_accepted", ct)
                .ConfigureAwait(false);
        await RollbackCreatedActorsAsync(attempt.CreatedActorIds, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RollbackCreatedActorsAsync(
        IReadOnlyList<string>? createdActorIds,
        CancellationToken ct)
    {
        if (createdActorIds is not { Count: > 0 })
            return;

        foreach (var actorId in createdActorIds
                     .Where(static x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal)
                     .Reverse())
        {
            await _runProvisioningPort.DestroyAsync(actorId, ct).ConfigureAwait(false);
        }
    }

    private static string CreateInteractionId() => Guid.NewGuid().ToString("N");

    private sealed record WorkflowChatRunInteractionAttempt(
        WorkflowChatRunRequest Request,
        IReadOnlyList<string> CreatedActorIds)
    {
        public bool Accepted { get; private set; }
        public bool CleanedUp { get; private set; }

        public void MarkAccepted() => Accepted = true;

        public bool TryMarkCleanedUp()
        {
            if (CleanedUp)
                return false;
            CleanedUp = true;
            return true;
        }
    }

    private sealed record AttemptStartResult(
        WorkflowChatRunInteractionAttempt? Value,
        WorkflowChatRunStartError Error)
    {
        public bool Succeeded => Error == WorkflowChatRunStartError.None && Value != null;

        public static AttemptStartResult Success(WorkflowChatRunInteractionAttempt attempt) =>
            new(attempt, WorkflowChatRunStartError.None);

        public static AttemptStartResult Failure(WorkflowChatRunStartError error) =>
            new(null, error);
    }
}
