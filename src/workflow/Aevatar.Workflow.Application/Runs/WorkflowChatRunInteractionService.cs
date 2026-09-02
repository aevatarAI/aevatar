using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
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
    private readonly IWorkflowChatHistoryCreateRecoveryReadPort? _chatHistoryCreateRecoveryReadPort;
    private readonly IWorkflowExecutionCurrentStateQueryPort? _currentStateQueryPort;
    private readonly ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>? _signalDispatchService;
    private readonly ICommandFinalizeEmitter<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus, WorkflowRunEventEnvelope>? _finalizeEmitter;
    private readonly WorkflowRunBehaviorOptions _behaviorOptions;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public WorkflowChatRunInteractionService(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowExecutionProjectionPort projectionPort,
        IWorkflowRunProvisioningPort runProvisioningPort,
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> inner,
        WorkflowDirectFallbackPolicy fallbackPolicy,
        IWorkflowChatHistoryTerminalDeliveryPort? chatHistoryTerminalDeliveryPort = null,
        IWorkflowChatHistoryCreateRecoveryReadPort? chatHistoryCreateRecoveryReadPort = null,
        WorkflowRunBehaviorOptions? behaviorOptions = null,
        IWorkflowExecutionCurrentStateQueryPort? currentStateQueryPort = null,
        ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>? signalDispatchService = null,
        ICommandFinalizeEmitter<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus, WorkflowRunEventEnvelope>? finalizeEmitter = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _actorResolver = actorResolver ?? throw new ArgumentNullException(nameof(actorResolver));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _runProvisioningPort = runProvisioningPort ?? throw new ArgumentNullException(nameof(runProvisioningPort));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _fallbackPolicy = fallbackPolicy ?? throw new ArgumentNullException(nameof(fallbackPolicy));
        _chatHistoryTerminalDeliveryPort = chatHistoryTerminalDeliveryPort;
        _chatHistoryCreateRecoveryReadPort = chatHistoryCreateRecoveryReadPort;
        _currentStateQueryPort = currentStateQueryPort;
        _signalDispatchService = signalDispatchService;
        _finalizeEmitter = finalizeEmitter;
        _behaviorOptions = behaviorOptions ?? new WorkflowRunBehaviorOptions();
        _delayAsync = delayAsync ?? ((delay, token) => Task.Delay(delay, token));
    }

    public async Task<WorkflowChatRunInteractionResult> ExecuteAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
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
        var currentTurnId = ResolveCurrentTurnId(request);
        var currentRequest = request with
        {
            CommandIdSeed = commandId,
            CorrelationIdSeed = correlationId,
            CurrentTurnId = currentTurnId,
            TargetSeed = null,
        };

        var preflight = ValidatePreActorResolution(currentRequest);
        if (preflight != WorkflowChatRunStartError.None)
            return WorkflowChatRunInteractionResult.Failure(
                preflight,
                WorkflowChatRunStartFailureDetail.Create(preflight));

        var recoveredCreate = await TryRecoverCreateAsync(currentRequest, ct).ConfigureAwait(false);
        if (recoveredCreate != null)
            return recoveredCreate;

        var signaledContinuation = await TrySignalWaitingConversationAsync(
            currentRequest,
            emitAsync,
            onAcceptedAsync,
            ct).ConfigureAwait(false);
        if (signaledContinuation != null)
            return signaledContinuation;

        while (true)
        {
            var attempt = await StartAttemptAsync(currentRequest, ct).ConfigureAwait(false);
            if (!attempt.Succeeded)
                return WorkflowChatRunInteractionResult.Failure(attempt.Error, attempt.FailureDetail);

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
                    CurrentTurnId = currentTurnId,
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
            return AttemptStartResult.Failure(
                WorkflowChatRunStartError.ProjectionDisabled,
                WorkflowChatRunStartFailureDetail.Create(WorkflowChatRunStartError.ProjectionDisabled));

        if (Aevatar.Workflow.Abstractions.WorkflowCallerCredentialTokens.IsInvalidCredentialSet(
                request.CallerCredential?.BearerToken,
                request.CallerCredential?.Kind ??
                Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind.Unspecified,
                request.CallerCredential?.SourceReadableUserBearerToken))
            return AttemptStartResult.Failure(
                WorkflowChatRunStartError.InvalidCallerCredential,
                WorkflowChatRunStartFailureDetail.Create(WorkflowChatRunStartError.InvalidCallerCredential));

        var actorResolution = await _actorResolver.ResolveOrCreateAsync(request, ct).ConfigureAwait(false);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Target == null)
            return AttemptStartResult.Failure(
                actorResolution.Error,
                actorResolution.FailureDetail ?? WorkflowChatRunStartFailureDetail.Create(actorResolution.Error));

        var seededRequest = request with
        {
            TargetSeed = new WorkflowRunTargetSeed(
                actorResolution.Target.ActorId,
                actorResolution.WorkflowNameForRun,
                actorResolution.Target.CreatedActorIds,
                request.Source),
            CallerNyxIdCredentialSelection = null,
        };

        return AttemptStartResult.Success(new WorkflowChatRunInteractionAttempt(
            seededRequest,
            actorResolution.Target.CreatedActorIds));
    }

    private async Task<WorkflowChatRunInteractionResult> ExecuteAttemptAsync(
        WorkflowChatRunInteractionAttempt attempt,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
        CancellationToken ct)
    {
        var chatHistoryDelivery = await ReserveChatHistoryDeliveryAsync(attempt.Request, ct).ConfigureAwait(false);
        if (chatHistoryDelivery is { Succeeded: false })
        {
            await CleanupAttemptAsync(attempt, CancellationToken.None).ConfigureAwait(false);
            var error = MapReservationFailure(chatHistoryDelivery.Failure);
            return WorkflowChatRunInteractionResult.Failure(
                error,
                WorkflowChatRunStartFailureDetail.Create(error));
        }
        if (chatHistoryDelivery is { Reservation.ExistingReservation: true } &&
            attempt.Request.ChatConversation?.Intent == WorkflowChatConversationIntentKind.Create)
        {
            await CleanupAttemptAsync(attempt, CancellationToken.None).ConfigureAwait(false);
            return WorkflowChatRunInteractionResult.Failure(
                WorkflowChatRunStartError.ChatHistoryReservationUnavailable,
                WorkflowChatRunStartFailureDetail.Create(WorkflowChatRunStartError.ChatHistoryReservationUnavailable));
        }

        async ValueTask OnAcceptedAsync(WorkflowChatRunAcceptedReceipt receipt, CancellationToken token)
        {
            attempt.MarkAccepted();
            if (chatHistoryDelivery is { Reservation: not null })
                await _chatHistoryTerminalDeliveryPort!
                    .BindAcceptedAsync(chatHistoryDelivery.Reservation, receipt, token)
                    .ConfigureAwait(false);
            if (onAcceptedAsync != null)
                await onAcceptedAsync(new WorkflowChatInteractionAcceptedReceipt(
                    receipt,
                    chatHistoryDelivery?.ChatContext), token).ConfigureAwait(false);
        }

        var conversationContext = BuildDispatchConversationContext(chatHistoryDelivery);
        var dispatchRequest = attempt.Request with
        {
            CurrentTurnId = ResolveDispatchCurrentTurnId(attempt.Request, chatHistoryDelivery),
        };
        if (chatHistoryDelivery?.Reservation is { } reservation)
        {
            dispatchRequest = dispatchRequest with
            {
                CompletionNotificationTarget = CreateCompletionNotificationTarget(reservation),
                ConversationContext = conversationContext,
            };
        }
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
                await CleanupAttemptAsync(attempt, CancellationToken.None, chatHistoryDelivery?.Reservation).ConfigureAwait(false);
            throw;
        }

        if (!result.Succeeded && !attempt.Accepted)
            await CleanupAttemptAsync(attempt, CancellationToken.None, chatHistoryDelivery?.Reservation).ConfigureAwait(false);

        if (!result.Succeeded || result.Receipt == null)
            return WorkflowChatRunInteractionResult.Failure(
                result.Error,
                WorkflowChatRunStartFailureDetail.Create(result.Error));

        return WorkflowChatRunInteractionResult.Success(
            new WorkflowChatInteractionAcceptedReceipt(result.Receipt, chatHistoryDelivery?.ChatContext),
            result.FinalizeResult ?? new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                result.Completion,
                result.Completed));
    }

    private async Task<WorkflowChatHistoryTerminalDeliveryReservationResult?> ReserveChatHistoryDeliveryAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct)
    {
        var conversation = NormalizeConversationIntent(request);
        if (conversation is null)
            return null;

        if (_chatHistoryTerminalDeliveryPort is null ||
            request.TargetSeed is null ||
            string.IsNullOrWhiteSpace(request.ScopeId) ||
            string.IsNullOrWhiteSpace(request.Prompt) ||
            string.IsNullOrWhiteSpace(request.CommandIdSeed))
            return WorkflowChatHistoryTerminalDeliveryReservationResult.Unavailable();

        var normalizedScopeId = request.ScopeId.Trim();
        var normalizedCommandId = request.CommandIdSeed.Trim();
        var deliveryId = conversation.Intent == WorkflowChatConversationIntentKind.Create
            ? WorkflowChatHistoryCreateRecoveryIds.FromScopeAndCommandId(normalizedScopeId, normalizedCommandId)
            : $"chat-history-delivery-{request.TargetSeed.ActorId.Trim()}-{normalizedCommandId}";
        var requestFingerprint = conversation.Intent == WorkflowChatConversationIntentKind.Create
            ? WorkflowChatCreateRequestFingerprint.Compute(request with
            {
                ChatConversation = WorkflowChatConversationIntent.Create(),
                ScopeId = normalizedScopeId,
                CommandIdSeed = normalizedCommandId,
                TargetSeed = null,
                CompletionNotificationTarget = null,
            })
            : string.Empty;
        var result = await _chatHistoryTerminalDeliveryPort.ReserveAsync(
                new WorkflowChatHistoryTerminalDeliveryReservationRequest(
                    deliveryId,
                    normalizedScopeId,
                    conversation,
                    request.Prompt.Trim(),
                    request.TargetSeed.ActorId.Trim(),
                    normalizedCommandId,
                    request.CorrelationIdSeed?.Trim() ?? string.Empty,
                    requestFingerprint),
                ct)
            .ConfigureAwait(false);

        if (!result.Succeeded ||
            conversation.Intent != WorkflowChatConversationIntentKind.Create ||
            _chatHistoryCreateRecoveryReadPort is null)
        {
            return result;
        }

        try
        {
            if (await IsCreateReservationReadableAsync(
                    normalizedScopeId,
                    normalizedCommandId,
                    result,
                    requestFingerprint,
                    ct).ConfigureAwait(false))
            {
                return result;
            }
        }
        catch
        {
            await _chatHistoryTerminalDeliveryPort.AbandonAsync(
                    result.Reservation!,
                    "chat_history_reservation_observation_cancelled",
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        await _chatHistoryTerminalDeliveryPort.AbandonAsync(
                result.Reservation!,
                "chat_history_reservation_not_observed",
                CancellationToken.None)
            .ConfigureAwait(false);
        return WorkflowChatHistoryTerminalDeliveryReservationResult.Unavailable();
    }

    private async Task<bool> IsCreateReservationReadableAsync(
        string scopeId,
        string commandId,
        WorkflowChatHistoryTerminalDeliveryReservationResult reservation,
        string requestFingerprint,
        CancellationToken ct)
    {
        var interval = _behaviorOptions.ChatHistoryReservationObservationInterval;
        var timeout = _behaviorOptions.ChatHistoryReservationObservationTimeout;
        var maxPolls = Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds / interval.TotalMilliseconds));
        using var observationTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        observationTimeout.CancelAfter(timeout);
        var observationToken = observationTimeout.Token;

        try
        {
            for (var attempt = 0; attempt < maxPolls; attempt++)
            {
                observationToken.ThrowIfCancellationRequested();
                var observed = await _chatHistoryCreateRecoveryReadPort!
                    .GetByConversationAsync(
                        scopeId,
                        reservation.ChatContext!.ConversationId,
                        observationToken)
                    .ConfigureAwait(false);
                if (MatchesCreateReservation(
                        observed,
                        scopeId,
                        commandId,
                        reservation.ChatContext,
                        requestFingerprint))
                {
                    return true;
                }

                if (attempt + 1 < maxPolls)
                    await _delayAsync(interval, observationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && observationTimeout.IsCancellationRequested)
        {
            return false;
        }

        return false;
    }

    private static bool MatchesCreateReservation(
        WorkflowChatHistoryCreateRecovery? observed,
        string scopeId,
        string commandId,
        WorkflowChatContext context,
        string requestFingerprint) =>
        observed is not null &&
        observed.Status is not (WorkflowChatHistoryCreateRecoveryStatus.NotFound or
            WorkflowChatHistoryCreateRecoveryStatus.Abandoned) &&
        observed.StateVersion > 0 &&
        string.Equals(observed.ScopeId, scopeId, StringComparison.Ordinal) &&
        string.Equals(observed.CommandId, commandId, StringComparison.Ordinal) &&
        string.Equals(observed.ConversationId, context.ConversationId, StringComparison.Ordinal) &&
        string.Equals(observed.TurnId, context.TurnId, StringComparison.Ordinal) &&
        string.Equals(observed.RequestFingerprint, requestFingerprint, StringComparison.Ordinal);

    private async Task<WorkflowChatRunInteractionResult?> TryRecoverCreateAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct)
    {
        if (_chatHistoryCreateRecoveryReadPort is null ||
            request.ChatConversation?.Intent != WorkflowChatConversationIntentKind.Create ||
            string.IsNullOrWhiteSpace(request.ScopeId) ||
            string.IsNullOrWhiteSpace(request.CommandIdSeed))
        {
            return null;
        }

        var normalizedScopeId = request.ScopeId.Trim();
        var normalizedCommandId = request.CommandIdSeed.Trim();
        var requestFingerprint = WorkflowChatCreateRequestFingerprint.Compute(request with
        {
            ChatConversation = WorkflowChatConversationIntent.Create(),
            ScopeId = normalizedScopeId,
            CommandIdSeed = normalizedCommandId,
            TargetSeed = null,
            CompletionNotificationTarget = null,
        });
        var recovery = await _chatHistoryCreateRecoveryReadPort
            .GetAsync(normalizedScopeId, normalizedCommandId, ct)
            .ConfigureAwait(false);
        if (recovery == null)
            return null;

        if (!string.IsNullOrWhiteSpace(recovery.RequestFingerprint) &&
            !string.Equals(recovery.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return WorkflowChatRunInteractionResult.Failure(
                WorkflowChatRunStartError.IdempotencyConflict,
                WorkflowChatRunStartFailureDetail.Create(WorkflowChatRunStartError.IdempotencyConflict));
        }

        if (string.IsNullOrWhiteSpace(recovery.ConversationId) ||
            string.IsNullOrWhiteSpace(recovery.TurnId) ||
            string.IsNullOrWhiteSpace(recovery.WorkflowActorId))
        {
            return WorkflowChatRunInteractionResult.Failure(
                WorkflowChatRunStartError.ChatHistoryReservationUnavailable,
                WorkflowChatRunStartFailureDetail.Create(WorkflowChatRunStartError.ChatHistoryReservationUnavailable));
        }

        var receipt = new WorkflowChatInteractionAcceptedReceipt(
            new WorkflowChatRunAcceptedReceipt(
                recovery.WorkflowActorId.Trim(),
                request.Source.WorkflowName ?? WorkflowRunBehaviorOptions.DirectWorkflowName,
                string.IsNullOrWhiteSpace(recovery.WorkflowCommandId)
                    ? normalizedCommandId
                    : recovery.WorkflowCommandId.Trim(),
                string.IsNullOrWhiteSpace(recovery.WorkflowCorrelationId)
                    ? request.CorrelationIdSeed?.Trim() ?? normalizedCommandId
                    : recovery.WorkflowCorrelationId.Trim()),
            new WorkflowChatContext(
                normalizedScopeId,
                recovery.ConversationId.Trim(),
                recovery.TurnId.Trim(),
                recovery.StateVersion));

        return WorkflowChatRunInteractionResult.Success(
            receipt,
            new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                WorkflowProjectionCompletionStatus.Completed,
                false));
    }

    private async Task<WorkflowChatRunInteractionResult?> TrySignalWaitingConversationAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
        CancellationToken ct)
    {
        if (_chatHistoryCreateRecoveryReadPort is null ||
            _currentStateQueryPort is null ||
            _signalDispatchService is null ||
            _finalizeEmitter is null ||
            request.ChatConversation?.Intent != WorkflowChatConversationIntentKind.Continue ||
            string.IsNullOrWhiteSpace(request.ScopeId) ||
            string.IsNullOrWhiteSpace(request.ChatConversation.ConversationId) ||
            string.IsNullOrWhiteSpace(request.Prompt))
        {
            return null;
        }

        if (!_projectionPort.ProjectionEnabled)
            return WorkflowChatRunInteractionResult.Failure(
                WorkflowChatRunStartError.ProjectionDisabled,
                WorkflowChatRunStartFailureDetail.Create(WorkflowChatRunStartError.ProjectionDisabled));

        var normalizedScopeId = request.ScopeId.Trim();
        var conversationId = request.ChatConversation.ConversationId.Trim();
        var recovery = await _chatHistoryCreateRecoveryReadPort
            .GetByConversationAsync(normalizedScopeId, conversationId, ct)
            .ConfigureAwait(false);
        if (recovery == null || string.IsNullOrWhiteSpace(recovery.WorkflowActorId))
            return null;

        var actorId = recovery.WorkflowActorId.Trim();
        var snapshot = await _currentStateQueryPort
            .GetWorkflowActorCurrentStateAsync(actorId, ct)
            .ConfigureAwait(false);
        if (!IsWaitingForSignal(snapshot, normalizedScopeId, out var runId, out var stepId, out var signalName))
            return null;

        var commandId = string.IsNullOrWhiteSpace(request.CommandIdSeed)
            ? CreateInteractionId()
            : request.CommandIdSeed.Trim();
        var observationSessionId = string.IsNullOrWhiteSpace(recovery.WorkflowCommandId)
            ? commandId
            : recovery.WorkflowCommandId.Trim();
        var signalCommand = new WorkflowSignalCommand(
            actorId,
            runId,
            signalName,
            commandId,
            request.Prompt.Trim(),
            stepId,
            request.CorrelationIdSeed);
        var signalSink = new EventChannel<WorkflowRunEventEnvelope>();
        var attachment = await _projectionPort
            .AttachExistingActorProjectionAsync(actorId, observationSessionId, signalSink, ct)
            .ConfigureAwait(false);
        if (attachment == null)
        {
            await signalSink.DisposeAsync().ConfigureAwait(false);
            return WorkflowChatRunInteractionResult.Failure(
                WorkflowChatRunStartError.ProjectionUnavailable,
                WorkflowChatRunStartFailureDetail.Create(WorkflowChatRunStartError.ProjectionUnavailable));
        }

        try
        {
            var dispatch = await _signalDispatchService.DispatchAsync(signalCommand, ct).ConfigureAwait(false);
            if (!dispatch.Succeeded || dispatch.Receipt == null)
            {
                return WorkflowChatRunInteractionResult.Failure(
                    WorkflowChatRunStartError.ChatHistoryReservationUnavailable,
                    WorkflowChatRunStartFailureDetail.Create(
                        WorkflowChatRunStartError.ChatHistoryReservationUnavailable,
                        "Pending workflow signal could not be accepted."));
            }

            var acceptedReceipt = new WorkflowChatRunAcceptedReceipt(
                actorId,
                snapshot!.WorkflowName,
                dispatch.Receipt.CommandId,
                dispatch.Receipt.CorrelationId);
            var receipt = new WorkflowChatInteractionAcceptedReceipt(
                acceptedReceipt,
                new WorkflowChatContext(
                    normalizedScopeId,
                    conversationId,
                    request.CurrentTurnId ?? string.Empty,
                    snapshot.StateVersion));
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct).ConfigureAwait(false);

            var finalizeResult = await PumpSignaledContinuationAsync(
                signalSink,
                acceptedReceipt,
                emitAsync,
                ct).ConfigureAwait(false);
            return WorkflowChatRunInteractionResult.Success(receipt, finalizeResult);
        }
        finally
        {
            await _projectionPort.DetachReleaseAndDisposeAsync(
                    attachment.ProjectionLease,
                    attachment.LiveSinkLease,
                    signalSink,
                    ct: CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>> PumpSignaledContinuationAsync(
        IEventSink<WorkflowRunEventEnvelope> signalSink,
        WorkflowChatRunAcceptedReceipt receipt,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct)
    {
        var completed = false;
        var completion = WorkflowProjectionCompletionStatus.Unknown;
        await foreach (var evt in signalSink.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await emitAsync(evt, ct).ConfigureAwait(false);
            if (!TryResolveCompletion(evt, out var resolvedCompletion))
                continue;

            completed = true;
            completion = resolvedCompletion;
            break;
        }

        await _finalizeEmitter!.EmitAsync(receipt, completion, completed, emitAsync, ct).ConfigureAwait(false);
        return new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(completion, completed);
    }

    private static bool TryResolveCompletion(
        WorkflowRunEventEnvelope evt,
        out WorkflowProjectionCompletionStatus completion)
    {
        completion = WorkflowProjectionCompletionStatus.Unknown;
        if (evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunFinished)
        {
            completion = WorkflowProjectionCompletionStatus.Completed;
            return true;
        }

        if (evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunError)
        {
            completion = WorkflowProjectionCompletionStatus.Failed;
            return true;
        }

        if (evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunStopped)
        {
            completion = WorkflowProjectionCompletionStatus.Stopped;
            return true;
        }

        return false;
    }

    private static bool IsWaitingForSignal(
        WorkflowActorSnapshot? snapshot,
        string scopeId,
        out string runId,
        out string stepId,
        out string signalName)
    {
        runId = string.Empty;
        stepId = string.Empty;
        signalName = string.Empty;
        if (snapshot == null ||
            snapshot.CompletionStatus != WorkflowRunCompletionStatus.WaitingForSignal ||
            !string.Equals(snapshot.ScopeId?.Trim(), scopeId, StringComparison.Ordinal) ||
            snapshot.ActivityWaiting == null ||
            !string.Equals(snapshot.ActivityWaiting.Availability, "available", StringComparison.Ordinal) ||
            !string.Equals(snapshot.ActivityWaiting.WaitingKind, "signal", StringComparison.Ordinal))
        {
            return false;
        }

        runId = string.IsNullOrWhiteSpace(snapshot.RunId)
            ? snapshot.ActorId
            : snapshot.RunId.Trim();
        stepId = snapshot.ActivityWaiting.StepId?.Trim() ?? string.Empty;
        signalName = snapshot.ActivityWaiting.Prompt?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(runId) &&
               !string.IsNullOrWhiteSpace(stepId) &&
               !string.IsNullOrWhiteSpace(signalName);
    }

    private static WorkflowChatConversationIntent? NormalizeConversationIntent(WorkflowChatRunRequest request)
    {
        if (request.ChatConversation is { Intent: WorkflowChatConversationIntentKind.Create })
            return WorkflowChatConversationIntent.Create();

        if (request.ChatConversation is { Intent: WorkflowChatConversationIntentKind.Continue } conversation)
        {
            var conversationId = string.IsNullOrWhiteSpace(conversation.ConversationId)
                ? null
                : conversation.ConversationId.Trim();
            return conversationId == null
                ? null
                : WorkflowChatConversationIntent.Continue(
                    conversationId,
                    conversation.MinimumStateVersion);
        }

        return null;
    }

    private static WorkflowChatRunStartError MapReservationFailure(
        WorkflowChatHistoryTerminalDeliveryReservationFailure failure) =>
        failure switch
        {
            WorkflowChatHistoryTerminalDeliveryReservationFailure.ConversationNotFound =>
                WorkflowChatRunStartError.ConversationNotFound,
            WorkflowChatHistoryTerminalDeliveryReservationFailure.Unavailable =>
                WorkflowChatRunStartError.ChatHistoryReservationUnavailable,
            _ => WorkflowChatRunStartError.ChatHistoryReservationUnavailable,
        };

    private static Application.Abstractions.Runs.WorkflowCompletionNotificationTarget CreateCompletionNotificationTarget(
        WorkflowChatHistoryTerminalDeliveryReservation reservation) =>
        new(
            reservation.DeliveryActorId,
            reservation.DeliveryId,
            DateTimeOffset.UtcNow.Add(ChatHistoryTerminalDeliveryLifetime).ToUnixTimeMilliseconds());

    private static WorkflowConversationExecutionContext? BuildDispatchConversationContext(
        WorkflowChatHistoryTerminalDeliveryReservationResult? delivery)
    {
        if (delivery?.ChatContext is not { } chatContext)
            return delivery?.ConversationContext;

        if (delivery.ConversationContext is { } existing)
            return existing with { CurrentTurnId = chatContext.TurnId };

        return new WorkflowConversationExecutionContext(
            chatContext.ScopeId,
            chatContext.ConversationId,
            chatContext.StateVersion,
            [],
            false,
            0,
            chatContext.TurnId);
    }

    private static string ResolveCurrentTurnId(WorkflowChatRunRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.CurrentTurnId))
            return request.CurrentTurnId.Trim();
        if (!string.IsNullOrWhiteSpace(request.ConversationContext?.CurrentTurnId))
            return request.ConversationContext.CurrentTurnId.Trim();

        return $"turn-{CreateInteractionId()}";
    }

    private static string ResolveDispatchCurrentTurnId(
        WorkflowChatRunRequest request,
        WorkflowChatHistoryTerminalDeliveryReservationResult? delivery) =>
        string.IsNullOrWhiteSpace(delivery?.ChatContext?.TurnId)
            ? request.CurrentTurnId ?? string.Empty
            : delivery.ChatContext.TurnId.Trim();

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

    private WorkflowChatRunStartError ValidatePreActorResolution(WorkflowChatRunRequest request)
    {
        if (!_projectionPort.ProjectionEnabled)
            return WorkflowChatRunStartError.ProjectionDisabled;
        if (request.ChatConversation is { Intent: WorkflowChatConversationIntentKind.Continue } conversation &&
            !string.IsNullOrWhiteSpace(conversation.ConversationId) &&
            conversation.MinimumStateVersion is not > 0)
        {
            return WorkflowChatRunStartError.ChatHistoryReservationUnavailable;
        }

        return Aevatar.Workflow.Abstractions.WorkflowCallerCredentialTokens.IsInvalidCredentialSet(
                request.CallerCredential?.BearerToken,
                request.CallerCredential?.Kind ??
                Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind.Unspecified,
                request.CallerCredential?.SourceReadableUserBearerToken)
            ? WorkflowChatRunStartError.InvalidCallerCredential
            : WorkflowChatRunStartError.None;
    }

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
        WorkflowChatRunStartError Error,
        WorkflowChatRunStartFailureDetail? FailureDetail = null)
    {
        public bool Succeeded => Error == WorkflowChatRunStartError.None && Value != null;

        public static AttemptStartResult Success(WorkflowChatRunInteractionAttempt attempt) =>
            new(attempt, WorkflowChatRunStartError.None);

        public static AttemptStartResult Failure(
            WorkflowChatRunStartError error,
            WorkflowChatRunStartFailureDetail? failureDetail = null) =>
            new(null, error, failureDetail);
    }
}
