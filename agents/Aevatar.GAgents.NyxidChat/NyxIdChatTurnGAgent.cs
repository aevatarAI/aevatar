using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;

namespace Aevatar.GAgents.NyxidChat;

[GAgent(NyxIdChatServiceDefaults.TurnGAgentKind)]
public sealed class NyxIdChatTurnGAgent : GAgentBase<NyxIdChatTurnGAgentState>
{
    internal static readonly TimeSpan RecoveryCredentialRetention = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RecoveryCredentialRenewalRetryDelay = TimeSpan.FromSeconds(30);
    private const int DeliveredOperationHistoryLimit = 32;
    internal static readonly TimeSpan OperationCompletionWatchdogMargin = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan OperationResultDeliveryWatchdogDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CanaryEffectFaultConsumedRetryDelay = TimeSpan.FromSeconds(5);
    private const string DispatchFailedCode = "NYXID_CHAT_OPERATION_DISPATCH_FAILED";
    private const string DispatchFailedMessage = "The operation could not be accepted for execution.";
    private const string ResultKeyMismatchCode = "NYXID_CHAT_OPERATION_RESULT_KEY_MISMATCH";
    private const string ResultKeyMismatchMessage = "The operation result identity did not match the admitted operation.";
    private const string InterruptedCode = "NYXID_CHAT_OPERATION_INTERRUPTED";
    private const string InterruptedMessage =
        "The operation was interrupted and was not replayed automatically.";
    private const string ResultDeliveryLostCode =
        "NYXID_CHAT_OPERATION_RESULT_DELIVERY_LOST";
    private const string ResultDeliveryLostMessage =
        "The operation completed, but its original result could not be recovered for delivery.";
    internal const string CanaryEffectFaultCode = "NYXID_CHAT_CANARY_EFFECT_DISPATCH_AMBIGUOUS";
    private const string CanaryEffectFaultMessage =
        "The external operation may have changed state and requires exact read-back.";

    private readonly INyxIdChatTurnOperationDispatchSession _operationDispatchSession;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly TimeSpan _operationCompletionWatchdogDelay;
    private readonly TimeProvider _timeProvider;
    private readonly ISecretVault? _secretVault;

    public NyxIdChatTurnGAgent(
        INyxIdChatTurnOperationDispatchPort operationDispatchPort,
        IActorDispatchPort actorDispatchPort,
        NyxIdToolOptions nyxIdToolOptions,
        TimeProvider timeProvider,
        ISecretVault? secretVault = null)
    {
        ArgumentNullException.ThrowIfNull(operationDispatchPort);
        _operationDispatchSession = operationDispatchPort.OpenSession() ??
                                    throw new InvalidOperationException(
                                        "The turn operation dispatch session is unavailable.");
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        ArgumentNullException.ThrowIfNull(nyxIdToolOptions);
        _operationCompletionWatchdogDelay =
            nyxIdToolOptions.EffectiveMaxRequestDuration + OperationCompletionWatchdogMargin;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _secretVault = secretVault;
    }

    protected override NyxIdChatTurnGAgentState TransitionState(
        NyxIdChatTurnGAgentState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<NyxIdChatTurnOperationAdmittedEvent>(ApplyAdmitted)
            .On<NyxIdChatTurnOperationDeliveryFencedEvent>(ApplyOperationDeliveryFenced)
            .On<NyxIdChatTurnEffectDispatchStartedEvent>(ApplyEffectDispatchStarted)
            .On<NyxIdChatTurnCanaryEffectFaultTriggeredEvent>(ApplyCanaryEffectFaultTriggered)
            .On<NyxIdChatTurnOperationReconciliationStartedEvent>(ApplyReconciliationStarted)
            .On<NyxIdChatTurnOperationCompletedEvent>(ApplyCompleted)
            .On<NyxIdChatTurnOperationResultRedeliveryAttemptedEvent>(ApplyResultRedeliveryAttempted)
            .On<NyxIdChatTurnOperationDeliveredEvent>(ApplyDelivered)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        if (State.CanaryEffectFaultConsumed)
            await TryDispatchCanaryEffectFaultConsumedAsync(ct);

        if (State.AdmittedOperation is not null && State.RecoveryCredential is not null &&
            (!State.ResultDelivered ||
             State.Phase == NyxIdChatOperationPhase.Uncertain && EffectMayHaveChanged(State) ||
             AwaitingFrozenVerification(State)))
        {
            await ScheduleRecoveryCredentialRenewalAsync(
                State.AdmittedOperation,
                State.RecoveryCredential,
                ct);
        }

        if (State.AdmittedOperation is null || State.ResultDelivered)
            return;

        if (State.CompletedAt is not null && IsTerminal(State.Phase))
            await RevokeRecoveryCredentialAsync(State.RecoveryCredential, ct);

        var version = CurrentCommittedVersion();
        var signal = new NyxIdChatRecoveryRequestedSignal
        {
            Key = State.AdmittedOperation.Clone(),
            ExpectedStateVersion = version,
            Kind = NyxIdChatRecoveryKind.InterruptedOperationReconciliation,
        };
        await PublishAsync(
            signal,
            TopologyAudience.Self,
            ct,
            new EventEnvelopePublishOptions
            {
                Propagation = new EventEnvelopePropagationOverrides
                {
                    CorrelationId = State.AdmittedOperation.OperationId,
                },
                Delivery = new EventEnvelopeDeliveryOptions
                {
                    OperationId =
                        $"{State.AdmittedOperation.OperationId}:turn-recovery:{version}",
                },
            });
    }

    [EventHandler]
    public async Task HandleOperationAsync(NyxIdChatOperationDispatchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsValid(command))
            return;

        if (KeysEqual(State.AdmittedOperation, command.Key))
        {
            if (State.CanaryEffectFaultConsumed)
                await TryDispatchCanaryEffectFaultConsumedAsync(CancellationToken.None);
            await DispatchOperationDeliveryStatusAsync(
                command.Key,
                admitted: true,
                State.EffectDispatchWaterline,
                CancellationToken.None);
            return;
        }

        if (State.FencedOperationDeliveries.Any(key => KeysEqual(key, command.Key)))
        {
            await DispatchOperationDeliveryStatusAsync(
                command.Key,
                admitted: false,
                NyxIdChatEffectEvidence.NotStarted,
                CancellationToken.None);
            return;
        }

        if (!MatchesContinuationAuthorization(command) || IsDuplicateOrUnavailable(command))
            return;

        var kind = ResolveKind(command);
        var admittedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var mayChangeExternalState = command.Tool?.MayChangeExternalState == true ||
                                     command.ToolApprovalContinuation?.MayChangeExternalState == true;
        var idempotent = command.Tool?.Idempotent == true;
        var idempotencyKey = ResolveIdempotencyKey(command);
        var operationAdmission = ResolveOperationAdmission(command);
        var toolContext = _operationDispatchSession.CaptureToolContext();
        var canaryEffectFault = NyxIdChatCanaryEffectFaultDecisions.MatchesTurnDispatch(
            command.Tool?.CanaryEffectFault,
            command,
            toolContext,
            admittedAt)
            ? command.Tool!.CanaryEffectFault.Clone()
            : null;
        await HydrateFrozenVerificationContextAsync(command);
        var recovery = await PrepareRecoveryAdmissionAsync(
            command,
            operationAdmission,
            toolContext);
        var admitted = new NyxIdChatTurnOperationAdmittedEvent
        {
            Key = command.Key.Clone(),
            OperationKind = kind,
            MayChangeExternalState = mayChangeExternalState,
            EffectDispatchWaterline = mayChangeExternalState
                ? NyxIdChatEffectEvidence.NotStarted
                : NyxIdChatEffectEvidence.NotApplied,
            Idempotent = idempotent,
            IdempotencyKey = idempotencyKey,
            OperationAdmission = operationAdmission?.Clone(),
            RecoveryEffectStepId = recovery.EffectStepId,
            ExactServiceRecoveryStage = recovery.ExactServiceStage,
            AdmittedAt = admittedAt,
        };
        if (recovery.Context is not null)
            admitted.RecoveryContext = recovery.Context;
        if (recovery.Credential is not null)
            admitted.RecoveryCredential = recovery.Credential;
        if (recovery.ReadBack is not null)
            admitted.RecoveryReadBack = recovery.ReadBack;
        if (canaryEffectFault is not null)
        {
            admitted.CanaryEffectFault = canaryEffectFault;
            admitted.CanaryEffectFaultToolCallId = command.Tool!.CallId;
            admitted.CanaryEffectFaultToolName = command.Tool.ToolName;
        }
        await PersistDomainEventAsync(admitted, CancellationToken.None);

        if (State.RecoveryCredential is not null)
        {
            await ScheduleRecoveryCredentialRenewalAsync(
                command.Key,
                State.RecoveryCredential,
                CancellationToken.None);
        }

        if (NyxIdChatTurnOperationDispatchPort.MayDispatchExternalEffect(command))
        {
            var dispatchStarted = new NyxIdChatTurnEffectDispatchStartedEvent
            {
                Key = command.Key.Clone(),
                StartedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            };
            await PersistDomainEventAsync(dispatchStarted, CancellationToken.None);
        }

        try
        {
            await _operationDispatchSession.DispatchExecutionAsync(
                Id,
                command,
                ActiveInboundEnvelope?.Propagation?.CorrelationId ?? command.Key.OperationId,
                CancellationToken.None);
            if (operationAdmission is not null)
                await ScheduleOperationCompletionWatchdogAsync(command.Key);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat turn operation dispatch failed: turnActor={TurnActorId} operation={OperationId}",
                Id,
                command.Key.OperationId);
            await CompleteAndDeliverAsync(new NyxIdChatOperationResultSignal
            {
                Key = command.Key.Clone(),
                Failure = new NyxIdChatOperationFailure
                {
                    FailureCode = DispatchFailedCode,
                    SafeMessage = DispatchFailedMessage,
                    ExternalEffect = EffectMayHaveChanged(State)
                        ? NyxIdChatEffectEvidence.MayHaveChanged
                        : NyxIdChatEffectEvidence.NotApplied,
                },
            });
        }

        await DispatchOperationDeliveryStatusAsync(
            command.Key,
            admitted: true,
            State.EffectDispatchWaterline,
            CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleOperationDeliveryProbeAsync(
        NyxIdChatTurnOperationDeliveryProbeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsValidOperationDeliveryProbe(command.Key))
            return;

        if (KeysEqual(State.AdmittedOperation, command.Key))
        {
            await DispatchOperationDeliveryStatusAsync(
                command.Key,
                admitted: true,
                State.EffectDispatchWaterline,
                CancellationToken.None);
            return;
        }

        if (!State.FencedOperationDeliveries.Any(key => KeysEqual(key, command.Key)))
        {
            await PersistDomainEventAsync(new NyxIdChatTurnOperationDeliveryFencedEvent
            {
                Key = command.Key.Clone(),
                FencedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, CancellationToken.None);
        }

        await DispatchOperationDeliveryStatusAsync(
            command.Key,
            admitted: false,
            NyxIdChatEffectEvidence.NotStarted,
            CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleOperationExecutionProgressAsync(
        NyxIdChatTurnOperationExecutionProgressSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Progress?.Key is null ||
            State.AdmittedOperation is null ||
            State.ResultDelivered ||
            IsTerminal(State.Phase) ||
            !KeysEqual(State.AdmittedOperation, signal.Progress.Key))
        {
            return;
        }

        await DispatchProgressAsync(
            State.AdmittedOperation,
            signal.Progress,
            CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleOperationCancelAsync(NyxIdChatTurnOperationCancelCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Key is null ||
            State.AdmittedOperation is null ||
            State.ResultDelivered ||
            IsTerminal(State.Phase) ||
            !KeysEqual(State.AdmittedOperation, command.Key))
        {
            return;
        }

        await _operationDispatchSession.CancelExecutionAsync(command.Key, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleOperationExecutionCompletedAsync(
        NyxIdChatTurnOperationExecutionCompletedSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Result?.Key is null ||
            signal.Source == NyxIdChatTurnOperationCompletionSource.Unspecified ||
            State.AdmittedOperation is null ||
            State.ResultDelivered ||
            IsTerminal(State.Phase) ||
            !KeysEqual(State.AdmittedOperation, signal.Result.Key) ||
            (signal.Source == NyxIdChatTurnOperationCompletionSource.Reconciliation &&
             State.ReconciliationStartedAt is null))
        {
            return;
        }

        await CompleteAndDeliverAsync(NormalizeResult(
            State.AdmittedOperation,
            signal.Result,
            EffectMayHaveChanged(State)));
    }

    [EventHandler]
    public async Task HandleOperationResultAcknowledgedAsync(
        NyxIdChatTurnOperationResultAcknowledgedSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Key is null ||
            State.AdmittedOperation is null ||
            State.PendingResult is null ||
            State.ResultDelivered ||
            !KeysEqual(State.AdmittedOperation, signal.Key) ||
            !KeysEqual(State.PendingResult.Key, signal.Key) ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(State.PendingResult.ToByteArray()),
                signal.ResultSha256.Span))
        {
            return;
        }

        await PersistDomainEventAsync(new NyxIdChatTurnOperationDeliveredEvent
        {
            Key = signal.Key.Clone(),
            DeliveredAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleCanaryEffectFaultTriggeredAsync(
        NyxIdChatCanaryEffectFaultTriggeredSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!IsValidCanaryEffectFaultTriggered(signal))
            return;

        var triggeredAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var receipt = signal.DeniedResult.Tool.Receipt;
        await PersistDomainEventAsync(new NyxIdChatTurnCanaryEffectFaultTriggeredEvent
        {
            Key = signal.DeniedResult.Key.Clone(),
            ArmId = signal.ArmId,
            ApprovalRequestId = receipt.ApprovalRequestId,
            TriggeredAt = triggeredAt,
            ReceiptStatus = receipt.Status,
            ApprovalDecisionMode = receipt.NyxIdApprovalDecisionMode,
            ApprovalTerminalOutcome = receipt.NyxIdApprovalTerminalOutcome,
            ApprovalSubjectKind = receipt.SubjectKind,
            ApprovalSubjectId = receipt.SubjectId,
            ApprovalCallId = receipt.CallId,
            ApprovalToolName = receipt.ToolName,
        }, CancellationToken.None);
        await TryDispatchCanaryEffectFaultConsumedAsync(CancellationToken.None);
        await CompleteAndDeliverAsync(CanaryEffectFaultResult(signal.DeniedResult.Key));
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleCanaryEffectFaultConsumedRetryAsync(
        NyxIdChatCanaryEffectFaultConsumedRetryRequested signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var directive = State.CanaryEffectFault;
        if (!State.CanaryEffectFaultConsumed ||
            directive?.Key is null ||
            signal.Key is null ||
            !string.Equals(directive.ArmId, signal.ArmId, StringComparison.Ordinal) ||
            !directive.Key.Equals(signal.Key))
        {
            return;
        }

        await TryDispatchCanaryEffectFaultConsumedAsync(CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleRecoveryRequestedAsync(NyxIdChatRecoveryRequestedSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Kind is not (NyxIdChatRecoveryKind.InterruptedOperationReconciliation or
            NyxIdChatRecoveryKind.OperationCompletionWatchdog or
            NyxIdChatRecoveryKind.OperationResultDeliveryWatchdog) ||
            signal.ExpectedStateVersion != CurrentCommittedVersion() ||
            signal.Key is null ||
            State.AdmittedOperation is null ||
            State.ResultDelivered ||
            !KeysEqual(State.AdmittedOperation, signal.Key))
        {
            return;
        }

        if (State.CanaryEffectFaultConsumed)
        {
            await TryDispatchCanaryEffectFaultConsumedAsync(CancellationToken.None);
            await CompleteAndDeliverAsync(CanaryEffectFaultResult(signal.Key));
            return;
        }

        var completedUndelivered = State.CompletedAt is not null && IsTerminal(State.Phase);
        if (completedUndelivered &&
            State.PendingResult?.Key is not null &&
            KeysEqual(State.PendingResult.Key, signal.Key))
        {
            await PersistDomainEventAsync(new NyxIdChatTurnOperationResultRedeliveryAttemptedEvent
            {
                Key = signal.Key.Clone(),
                Attempt = checked(State.ResultDeliveryAttempt + 1),
                AttemptedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, CancellationToken.None);
            await ScheduleOperationResultDeliveryWatchdogAsync(signal.Key);
            await DispatchResultAsync(signal.Key.ConversationActorId, State.PendingResult.Clone());
            return;
        }

        if (!completedUndelivered &&
            (EffectMayHaveChanged(State) ||
             State.ExactServiceRecoveryStage !=
             NyxIdChatExactServiceRecoveryStage.Unspecified ||
             State.OperationKind == NyxIdChatStepKind.Postcondition &&
             State.RecoveryReadBack is not null))
        {
            if (State.ReconciliationStartedAt is not null)
            {
                await CompleteAndDeliverAsync(OutcomeUncertain(signal.Key));
                return;
            }

            try
            {
                await PersistDomainEventAsync(new NyxIdChatTurnOperationReconciliationStartedEvent
                {
                    Key = signal.Key.Clone(),
                    StartedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                }, CancellationToken.None);
                await _operationDispatchSession.DispatchReconciliationAsync(
                    Id,
                    new NyxIdChatTurnOperationReconciliationInput
                    {
                        Key = signal.Key.Clone(),
                        OperationAdmission = State.OperationAdmission?.Clone(),
                        IdempotencyKey = State.IdempotencyKey,
                        EffectDispatchWaterline = State.EffectDispatchWaterline,
                        ReadBack = State.RecoveryReadBack?.Clone(),
                        ProviderResourceId = State.ProviderResourceId,
                        RecoveryContext = State.RecoveryContext?.Clone(),
                        RecoveryCredential = State.RecoveryCredential?.Clone(),
                        EffectStepId = State.RecoveryEffectStepId,
                        ExactServiceRecoveryStage = State.ExactServiceRecoveryStage,
                    },
                    ActiveInboundEnvelope?.Propagation?.CorrelationId ?? signal.Key.OperationId,
                    CancellationToken.None);
                await ScheduleOperationCompletionWatchdogAsync(signal.Key);
            }
            catch (Exception exception)
            {
                Logger.LogWarning(
                    exception,
                    "NyxIdChat operation reconciliation handoff failed: turnActor={TurnActorId} operation={OperationId}",
                    Id,
                    signal.Key.OperationId);
                await CompleteAndDeliverAsync(OutcomeUncertain(signal.Key));
            }
            return;
        }

        var effect = completedUndelivered
            ? NormalizeEffect(
                State.ExternalEffect,
                EffectMayHaveChanged(State)
                    ? NyxIdChatEffectEvidence.MayHaveChanged
                    : NyxIdChatEffectEvidence.NotApplied)
            : EffectMayHaveChanged(State)
                ? NyxIdChatEffectEvidence.MayHaveChanged
                : NyxIdChatEffectEvidence.NotApplied;
        var result = new NyxIdChatOperationResultSignal
        {
            Key = signal.Key.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = completedUndelivered ? ResultDeliveryLostCode : InterruptedCode,
                SafeMessage = completedUndelivered
                    ? ResultDeliveryLostMessage
                    : InterruptedMessage,
                ExternalEffect = effect,
            },
        };
        await CompleteAndDeliverAsync(result);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleRecoveryCredentialRenewalRequestedAsync(
        NyxIdChatRecoveryCredentialRenewalRequested signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (_secretVault is null ||
            signal.Key is null ||
            State.AdmittedOperation is null ||
            State.RecoveryCredential is null ||
            !KeysEqual(State.AdmittedOperation, signal.Key) ||
            !string.Equals(
                State.RecoveryCredential.Ref,
                signal.CredentialRef,
                StringComparison.Ordinal) ||
            State.ResultDelivered &&
            !(State.Phase == NyxIdChatOperationPhase.Uncertain && EffectMayHaveChanged(State)) &&
            !AwaitingFrozenVerification(State))
        {
            return;
        }

        var credential = State.RecoveryCredential.Clone();
        var resolved = await _secretVault.ResolveAsync(new ResolveSecretRequest(
            credential.Ref,
            CredentialSecretPurposes.NyxIdChatRecoveryCredential,
            credential.OwnerScopeKey,
            credential.SubjectId,
            "renew nyxid chat recovery credential"), CancellationToken.None);
        if (!resolved.Resolved ||
            !NyxIdChatRecoverySecretPayloadCodec.TryDecode(resolved.Secret, out var recoverySecret) ||
            recoverySecret.Credentials is not { } credentials ||
            credentials.NyxIdCredentialKind != AgentToolNyxIdCredentialKindPayload.ProxyDelegation)
        {
            return;
        }

        var renewal = await Services.GetRequiredService<INyxIdChatDelegationCredentialLifecyclePort>()
            .ResolveAsync(credentials.NyxIdAccessToken, CancellationToken.None);
        if (renewal.Succeeded && !string.IsNullOrWhiteSpace(renewal.AccessToken))
        {
            credentials.NyxIdAccessToken = renewal.AccessToken;
            if (renewal.Refreshed)
            {
                await _secretVault.RotateAsync(new RotateSecretRequest(
                    credential.Ref,
                    CredentialSecretPurposes.NyxIdChatRecoveryCredential,
                    credential.OwnerScopeKey,
                    credential.SubjectId,
                    NyxIdChatRecoverySecretPayloadCodec.Encode(recoverySecret, credentials),
                    "rotate renewed nyxid chat recovery credential"), CancellationToken.None);
            }

            await ScheduleRecoveryCredentialRenewalAsync(
                signal.Key,
                credential,
                CancellationToken.None);
            return;
        }

        await ScheduleSelfDurableTimeoutAsync(
            $"{signal.Key.OperationId}:recovery-credential-renewal-retry:{_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}",
            RecoveryCredentialRenewalRetryDelay,
            signal.Clone(),
            ct: CancellationToken.None);
    }

    private Task ScheduleOperationCompletionWatchdogAsync(NyxIdChatOperationKey key)
    {
        var version = CurrentCommittedVersion();
        return ScheduleSelfDurableTimeoutAsync(
            $"{key.OperationId}:operation-completion-watchdog:{version}",
            _operationCompletionWatchdogDelay,
            new NyxIdChatRecoveryRequestedSignal
            {
                Key = key.Clone(),
                ExpectedStateVersion = version,
                Kind = NyxIdChatRecoveryKind.OperationCompletionWatchdog,
            },
            new EventEnvelopePublishOptions
            {
                Propagation = new EventEnvelopePropagationOverrides
                {
                    CorrelationId = key.OperationId,
                },
                Delivery = new EventEnvelopeDeliveryOptions
                {
                    OperationId = $"{key.OperationId}:operation-completion-watchdog:{version}",
                },
            },
            CancellationToken.None);
    }

    private async Task ScheduleRecoveryCredentialRenewalAsync(
        NyxIdChatOperationKey key,
        DurableCallerCredentialRef credential,
        CancellationToken ct)
    {
        if (_secretVault is null)
            return;

        var resolved = await _secretVault.ResolveAsync(new ResolveSecretRequest(
            credential.Ref,
            CredentialSecretPurposes.NyxIdChatRecoveryCredential,
            credential.OwnerScopeKey,
            credential.SubjectId,
            "schedule nyxid chat recovery credential renewal"), ct);
        if (!resolved.Resolved ||
            !NyxIdChatRecoverySecretPayloadCodec.TryDecode(resolved.Secret, out var recoverySecret) ||
            recoverySecret.Credentials is not { } credentials ||
            credentials.NyxIdCredentialKind != AgentToolNyxIdCredentialKindPayload.ProxyDelegation ||
            !NyxIdDelegationTokenClaims.TryReadExpiry(
                credentials.NyxIdAccessToken,
                out var expiresAt))
        {
            return;
        }

        var due = expiresAt - _timeProvider.GetUtcNow() -
                  NyxIdChatDelegationCredentialLifecyclePort.RefreshWindow;
        if (due <= TimeSpan.Zero)
            due = TimeSpan.FromMilliseconds(1);
        await ScheduleSelfDurableTimeoutAsync(
            $"{key.OperationId}:recovery-credential-renewal:{expiresAt.ToUnixTimeSeconds()}",
            due,
            new NyxIdChatRecoveryCredentialRenewalRequested
            {
                Key = key.Clone(),
                CredentialRef = credential.Ref,
            },
            ct: ct);
    }

    private Task ScheduleOperationResultDeliveryWatchdogAsync(NyxIdChatOperationKey key)
    {
        var version = CurrentCommittedVersion();
        return ScheduleSelfDurableTimeoutAsync(
            $"{key.OperationId}:operation-result-delivery-watchdog:{version}",
            OperationResultDeliveryWatchdogDelay,
            new NyxIdChatRecoveryRequestedSignal
            {
                Key = key.Clone(),
                ExpectedStateVersion = version,
                Kind = NyxIdChatRecoveryKind.OperationResultDeliveryWatchdog,
            },
            new EventEnvelopePublishOptions
            {
                Propagation = new EventEnvelopePropagationOverrides
                {
                    CorrelationId = key.OperationId,
                },
                Delivery = new EventEnvelopeDeliveryOptions
                {
                    OperationId = $"{key.OperationId}:operation-result-delivery-watchdog:{version}",
                },
            },
            CancellationToken.None);
    }

    private static NyxIdChatOperationResultSignal OutcomeUncertain(
        NyxIdChatOperationKey key) => new()
    {
        Key = key.Clone(),
        Failure = new NyxIdChatOperationFailure
        {
            FailureCode = UnavailableNyxIdChatTurnOperationReconciliationPort.OutcomeUncertainCode,
            SafeMessage = "The external operation may have changed state and could not be reconciled.",
            ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
        },
    };

    private static NyxIdChatOperationResultSignal CanaryEffectFaultResult(
        NyxIdChatOperationKey key) => new()
    {
        Key = key.Clone(),
        Failure = new NyxIdChatOperationFailure
        {
            FailureCode = CanaryEffectFaultCode,
            SafeMessage = CanaryEffectFaultMessage,
            ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
        },
    };

    private bool IsDuplicateOrUnavailable(NyxIdChatOperationDispatchCommand command)
    {
        var key = command.Key;
        if (State.DeliveredOperations.Any(delivered => KeysEqual(delivered, key)))
            return true;

        if (State.AdmittedOperation is null)
            return false;

        if (KeysEqual(State.AdmittedOperation, key))
            return true;

        if (!State.ResultDelivered || !IsTerminal(State.Phase))
            return true;

        if (State.Phase == NyxIdChatOperationPhase.Uncertain && EffectMayHaveChanged(State))
            return !IsFrozenUncertainEffectVerification(command);

        return !SameTurn(State.AdmittedOperation, key);
    }

    private bool IsFrozenUncertainEffectVerification(NyxIdChatOperationDispatchCommand command)
    {
        var verification = command.ToolVerification;
        return verification is not null &&
               State.ResultDelivered &&
               State.RecoveryContext is not null &&
               State.RecoveryCredential is not null &&
               State.RecoveryReadBack is not null &&
               NyxIdChatOperationAdmissionPolicy.IsValidReadBack(State.RecoveryReadBack) &&
               verification.ReadBack is not null &&
               verification.ReadBack.Equals(State.RecoveryReadBack) &&
               string.Equals(
                   verification.EffectStepId,
                   State.RecoveryEffectStepId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   verification.ProviderResourceId,
                   State.ProviderResourceId,
                   StringComparison.Ordinal) &&
               SameTask(State.AdmittedOperation!, command.Key) &&
               !string.Equals(
                   State.AdmittedOperation!.OperationId,
                   command.Key.OperationId,
                   StringComparison.Ordinal);
    }

    private async Task DispatchProgressAsync(
        NyxIdChatOperationKey admittedKey,
        NyxIdChatOperationProgressSignal progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!KeysEqual(admittedKey, progress.Key))
            return;

        var envelope = CreateDirectEnvelope(
            admittedKey.ConversationActorId,
            $"{admittedKey.OperationId}:progress:{progress.Sequence}",
            progress);
        await _actorDispatchPort
            .DispatchAsync(admittedKey.ConversationActorId, envelope, ct);
    }

    private async Task CompleteAndDeliverAsync(NyxIdChatOperationResultSignal result)
    {
        var completion = ClassifyCompletion(result);
        var completed = new NyxIdChatTurnOperationCompletedEvent
        {
            Key = result.Key.Clone(),
            Phase = completion.Phase,
            TerminalCode = completion.TerminalCode,
            SafeMessage = completion.SafeMessage,
            ExternalEffect = completion.ExternalEffect,
            CompletedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            ProviderResourceId = result.Tool?.Receipt?.ProviderResourceId ?? string.Empty,
            ToolReceiptStatus = result.Tool?.Receipt?.Status ??
                                AgentToolReceiptStatus.Unspecified,
        };
        var requiresParentCommitAcknowledgement =
            State.OperationKind == NyxIdChatStepKind.Postcondition &&
            IsCredentialFreePostconditionTerminal(result);
        if (requiresParentCommitAcknowledgement)
            completed.Result = result.Clone();
        await PersistDomainEventAsync(completed, CancellationToken.None);
        var awaitsVerification = result.Tool?.Receipt?.Status == AgentToolReceiptStatus.Success &&
                                 State.RecoveryCredential is not null &&
                                 NyxIdChatOperationAdmissionPolicy.IsValidReadBack(
                                     State.RecoveryReadBack);
        if (!awaitsVerification &&
            (completion.Phase != NyxIdChatOperationPhase.Uncertain ||
             completion.ExternalEffect != NyxIdChatEffectEvidence.MayHaveChanged))
        {
            await RevokeRecoveryCredentialAsync(State.RecoveryCredential, CancellationToken.None);
        }
        await ScheduleOperationResultDeliveryWatchdogAsync(result.Key);
        await DispatchResultAsync(result.Key.ConversationActorId, result);
        if (!requiresParentCommitAcknowledgement)
        {
            await PersistDomainEventAsync(new NyxIdChatTurnOperationDeliveredEvent
            {
                Key = result.Key.Clone(),
                DeliveredAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, CancellationToken.None);
        }
    }

    private async Task HydrateFrozenVerificationContextAsync(
        NyxIdChatOperationDispatchCommand command)
    {
        if (command.ToolVerification is null ||
            command.ToolVerification.ToolContext is not null ||
            _secretVault is null ||
            State.RecoveryContext is null ||
            State.RecoveryCredential is null)
        {
            return;
        }

        var credential = State.RecoveryCredential;
        var resolved = await _secretVault.ResolveAsync(new ResolveSecretRequest(
            credential.Ref,
            CredentialSecretPurposes.NyxIdChatRecoveryCredential,
            credential.OwnerScopeKey,
            credential.SubjectId,
            "execute frozen nyxid chat effect verification"), CancellationToken.None);
        if (!resolved.Resolved ||
            !NyxIdChatRecoverySecretPayloadCodec.TryDecode(resolved.Secret, out var recoverySecret))
            return;

        var credentials = recoverySecret.Credentials;

        var credentialContext = AgentToolExecutionContextMapper.FromPayload(
            new AgentToolExecutionContextPayload { Credentials = credentials });
        var context = AgentToolExecutionContextMapper.FromRecoveryPayload(State.RecoveryContext) with
        {
            Credentials = credentialContext.Credentials,
        };
        command.ToolVerification.ToolContext = context.ToPayload();
    }

    private static bool AwaitingFrozenVerification(NyxIdChatTurnGAgentState state) =>
        state.ResultDelivered &&
        state.Phase == NyxIdChatOperationPhase.Succeeded &&
        state.ExternalEffect == NyxIdChatEffectEvidence.Confirmed &&
        state.RecoveryCredential is not null &&
        NyxIdChatOperationAdmissionPolicy.IsValidReadBack(state.RecoveryReadBack);

    private async Task DispatchResultAsync(
        string conversationActorId,
        NyxIdChatOperationResultSignal result)
    {
        var envelope = CreateDirectEnvelope(
            conversationActorId,
            $"{result.Key.OperationId}:result",
            result);
        await _actorDispatchPort
            .DispatchAsync(conversationActorId, envelope, CancellationToken.None);
    }

    private async Task RevokeRecoveryCredentialAsync(
        DurableCallerCredentialRef? credential,
        CancellationToken ct)
    {
        if (_secretVault is null || credential is null)
            return;
        try
        {
            await _secretVault.RevokeAsync(new RevokeSecretRequest(
                credential.Ref,
                CredentialSecretPurposes.NyxIdChatRecoveryCredential,
                credential.OwnerScopeKey,
                credential.SubjectId,
                "nyxid chat operation reached terminal state"), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat terminal recovery credential revocation failed: turnActor={TurnActorId} operation={OperationId}",
                Id,
                State.AdmittedOperation?.OperationId);
        }
    }

    private EventEnvelope CreateDirectEnvelope(string actorId, string envelopeId, IMessage payload) =>
        new()
        {
            Id = envelopeId,
            Timestamp = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            Payload = Any.Pack(payload),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actorId },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = ActiveInboundEnvelope?.Propagation?.CorrelationId
                    ?? payload.Descriptor.FullName,
            },
        };

    private static NyxIdChatOperationResultSignal NormalizeResult(
        NyxIdChatOperationKey admittedKey,
        NyxIdChatOperationResultSignal? result,
        bool effectMayHaveChanged)
    {
        if (result is not null && KeysEqual(admittedKey, result.Key))
            return result.Clone();

        return new NyxIdChatOperationResultSignal
        {
            Key = admittedKey.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = ResultKeyMismatchCode,
                SafeMessage = ResultKeyMismatchMessage,
                ExternalEffect = effectMayHaveChanged
                    ? NyxIdChatEffectEvidence.MayHaveChanged
                    : NyxIdChatEffectEvidence.NotApplied,
            },
        };
    }

    private static OperationCompletion ClassifyCompletion(NyxIdChatOperationResultSignal result)
    {
        if (result.Failure is not null)
        {
            return new OperationCompletion(
                result.Failure.ExternalEffect == NyxIdChatEffectEvidence.MayHaveChanged
                    ? NyxIdChatOperationPhase.Uncertain
                    : NyxIdChatOperationPhase.Failed,
                result.Failure.FailureCode,
                result.Failure.SafeMessage,
                NormalizeEffect(result.Failure.ExternalEffect, NyxIdChatEffectEvidence.NotApplied));
        }

        if (result.Tool is not null)
        {
            var receipt = result.Tool.Receipt;
            var phase = receipt?.Status switch
            {
                Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success or
                Aevatar.AI.Abstractions.AgentToolReceiptStatus.ApprovalRequired or
                Aevatar.AI.Abstractions.AgentToolReceiptStatus.AuthorizationRequired =>
                    NyxIdChatOperationPhase.Succeeded,
                Aevatar.AI.Abstractions.AgentToolReceiptStatus.Denied =>
                    NyxIdChatOperationPhase.Cancelled,
                _ => NyxIdChatOperationPhase.Failed,
            };
            return new OperationCompletion(
                phase,
                receipt?.ErrorCode ?? string.Empty,
                receipt?.ErrorMessage ?? string.Empty,
                NormalizeEffect(result.Tool.ExternalEffect, NyxIdChatEffectEvidence.NotApplied));
        }

        if (result.ActionPostcondition is not null)
        {
            return new OperationCompletion(
                result.ActionPostcondition.Verified
                    ? NyxIdChatOperationPhase.Succeeded
                    : NyxIdChatOperationPhase.Failed,
                result.ActionPostcondition.FailureCode,
                result.ActionPostcondition.SafeMessage,
                result.ActionPostcondition.Verified
                    ? NyxIdChatEffectEvidence.Confirmed
                    : NyxIdChatEffectEvidence.NotApplied);
        }

        if (result.ToolVerification is not null)
        {
            return result.ToolVerification.Disposition switch
            {
                NyxIdChatToolVerificationDisposition.Applied => new OperationCompletion(
                    NyxIdChatOperationPhase.Succeeded,
                    result.ToolVerification.FailureCode,
                    result.ToolVerification.SafeMessage,
                    NyxIdChatEffectEvidence.Confirmed),
                NyxIdChatToolVerificationDisposition.NotApplied => new OperationCompletion(
                    NyxIdChatOperationPhase.Succeeded,
                    result.ToolVerification.FailureCode,
                    result.ToolVerification.SafeMessage,
                    NyxIdChatEffectEvidence.NotApplied),
                _ => new OperationCompletion(
                    NyxIdChatOperationPhase.Uncertain,
                    result.ToolVerification.FailureCode,
                    result.ToolVerification.SafeMessage,
                    NyxIdChatEffectEvidence.MayHaveChanged),
            };
        }

        return result.Llm is not null
            ? new OperationCompletion(
                NyxIdChatOperationPhase.Succeeded,
                string.Empty,
                string.Empty,
                NyxIdChatEffectEvidence.NotApplied)
            : new OperationCompletion(
                NyxIdChatOperationPhase.Failed,
                DispatchFailedCode,
                DispatchFailedMessage,
                NyxIdChatEffectEvidence.NotApplied);
    }

    private static bool IsCredentialFreePostconditionTerminal(
        NyxIdChatOperationResultSignal result) =>
        result.ResultCase is
            NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition or
            NyxIdChatOperationResultSignal.ResultOneofCase.ToolVerification or
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure;

    private static NyxIdChatTurnGAgentState ApplyAdmitted(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnOperationAdmittedEvent evt)
    {
        var next = current.Clone();
        next.AdmittedOperation = evt.Key?.Clone();
        next.OperationKind = evt.OperationKind;
        next.Phase = NyxIdChatOperationPhase.Requested;
        next.TerminalCode = string.Empty;
        next.SafeMessage = string.Empty;
        next.ExternalEffect = NyxIdChatEffectEvidence.NotStarted;
        next.MayChangeExternalState = evt.MayChangeExternalState;
        next.EffectDispatchWaterline = evt.EffectDispatchWaterline;
        next.EffectDispatchStartedAt = null;
        next.Idempotent = evt.Idempotent;
        next.IdempotencyKey = evt.IdempotencyKey;
        next.OperationAdmission = evt.OperationAdmission?.Clone();
        next.ProviderResourceId = string.Empty;
        next.RecoveryContext = evt.RecoveryContext?.Clone();
        next.RecoveryCredential = evt.RecoveryCredential?.Clone();
        next.RecoveryReadBack = evt.RecoveryReadBack?.Clone();
        next.RecoveryEffectStepId = evt.RecoveryEffectStepId;
        next.ExactServiceRecoveryStage = evt.ExactServiceRecoveryStage;
        next.ToolReceiptStatus = AgentToolReceiptStatus.Unspecified;
        next.CanaryEffectFault = evt.CanaryEffectFault?.Clone();
        next.CanaryEffectFaultToolCallId = evt.CanaryEffectFaultToolCallId;
        next.CanaryEffectFaultToolName = evt.CanaryEffectFaultToolName;
        next.CanaryEffectFaultConsumed = false;
        next.CanaryEffectFaultConsumedAt = null;
        next.CanaryEffectFaultApprovalRequestId = string.Empty;
        next.CanaryEffectFaultReceiptStatus = AgentToolReceiptStatus.Unspecified;
        next.CanaryEffectFaultApprovalDecisionMode = NyxIdApprovalDecisionMode.Unspecified;
        next.CanaryEffectFaultApprovalTerminalOutcome = NyxIdApprovalTerminalOutcome.Unspecified;
        next.CanaryEffectFaultApprovalSubjectKind = string.Empty;
        next.CanaryEffectFaultApprovalSubjectId = string.Empty;
        next.ReconciliationStartedAt = null;
        next.ResultDelivered = false;
        next.PendingResult = null;
        next.ResultDeliveryAttempt = 0;
        next.AdmittedAt = evt.AdmittedAt?.Clone();
        next.CompletedAt = null;
        next.DeliveredAt = null;
        return next;
    }

    private static NyxIdChatTurnGAgentState ApplyOperationDeliveryFenced(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnOperationDeliveryFencedEvent evt)
    {
        if (evt.Key is null ||
            current.FencedOperationDeliveries.Any(key => KeysEqual(key, evt.Key)))
        {
            return current;
        }

        var next = current.Clone();
        next.FencedOperationDeliveries.Add(evt.Key.Clone());
        while (next.FencedOperationDeliveries.Count > DeliveredOperationHistoryLimit)
            next.FencedOperationDeliveries.RemoveAt(0);
        return next;
    }

    private static NyxIdChatTurnGAgentState ApplyEffectDispatchStarted(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnEffectDispatchStartedEvent evt)
    {
        if (!KeysEqual(current.AdmittedOperation, evt.Key) ||
            !current.MayChangeExternalState)
        {
            return current;
        }

        var next = current.Clone();
        next.EffectDispatchWaterline = NyxIdChatEffectEvidence.MayHaveChanged;
        next.EffectDispatchStartedAt = evt.StartedAt?.Clone();
        return next;
    }

    private static NyxIdChatTurnGAgentState ApplyCanaryEffectFaultTriggered(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnCanaryEffectFaultTriggeredEvent evt)
    {
        if (current.CanaryEffectFault is null ||
            current.CanaryEffectFaultConsumed ||
            evt.Key is null ||
            !KeysEqual(current.AdmittedOperation, evt.Key) ||
            !current.CanaryEffectFault.Key.Equals(evt.Key) ||
            !string.Equals(current.CanaryEffectFault.ArmId, evt.ArmId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(evt.ApprovalRequestId) ||
            evt.ReceiptStatus != AgentToolReceiptStatus.Denied ||
            evt.ApprovalTerminalOutcome != NyxIdApprovalTerminalOutcome.Rejected ||
            evt.ApprovalDecisionMode is not (
                NyxIdApprovalDecisionMode.Unspecified or
                NyxIdApprovalDecisionMode.PerRequest) ||
            !string.Equals(evt.ApprovalSubjectKind, "nyxid.user-service", StringComparison.Ordinal) ||
            !string.Equals(
                evt.ApprovalSubjectId,
                current.CanaryEffectFault.ServiceInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                evt.ApprovalCallId,
                current.CanaryEffectFaultToolCallId,
                StringComparison.Ordinal) ||
            !string.Equals(
                evt.ApprovalToolName,
                current.CanaryEffectFaultToolName,
                StringComparison.Ordinal) ||
            evt.TriggeredAt is null)
        {
            return current;
        }

        var next = current.Clone();
        next.CanaryEffectFaultConsumed = true;
        next.CanaryEffectFaultConsumedAt = evt.TriggeredAt.Clone();
        next.CanaryEffectFaultApprovalRequestId = evt.ApprovalRequestId;
        next.CanaryEffectFaultReceiptStatus = evt.ReceiptStatus;
        next.CanaryEffectFaultApprovalDecisionMode = evt.ApprovalDecisionMode;
        next.CanaryEffectFaultApprovalTerminalOutcome = evt.ApprovalTerminalOutcome;
        next.CanaryEffectFaultApprovalSubjectKind = evt.ApprovalSubjectKind;
        next.CanaryEffectFaultApprovalSubjectId = evt.ApprovalSubjectId;
        next.EffectDispatchWaterline = NyxIdChatEffectEvidence.MayHaveChanged;
        return next;
    }

    private static NyxIdChatTurnGAgentState ApplyReconciliationStarted(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnOperationReconciliationStartedEvent evt)
    {
        if (!KeysEqual(current.AdmittedOperation, evt.Key) ||
            !EffectMayHaveChanged(current) &&
            current.ExactServiceRecoveryStage ==
            NyxIdChatExactServiceRecoveryStage.Unspecified)
        {
            return current;
        }

        var next = current.Clone();
        next.ReconciliationStartedAt = evt.StartedAt?.Clone();
        return next;
    }

    private static NyxIdChatTurnGAgentState ApplyCompleted(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnOperationCompletedEvent evt)
    {
        if (!KeysEqual(current.AdmittedOperation, evt.Key))
            return current;

        var next = current.Clone();
        next.Phase = evt.Phase;
        next.TerminalCode = evt.TerminalCode;
        next.SafeMessage = evt.SafeMessage;
        next.ExternalEffect = evt.ExternalEffect;
        next.ProviderResourceId = evt.ProviderResourceId;
        next.ToolReceiptStatus = evt.ToolReceiptStatus;
        next.CompletedAt = evt.CompletedAt?.Clone();
        next.PendingResult = evt.Result?.Clone();
        next.ResultDeliveryAttempt = evt.Result is null ? 0 : 1;
        return next;
    }

    private static NyxIdChatTurnGAgentState ApplyResultRedeliveryAttempted(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnOperationResultRedeliveryAttemptedEvent evt)
    {
        if (!KeysEqual(current.AdmittedOperation, evt.Key) ||
            current.PendingResult is null ||
            current.ResultDelivered ||
            evt.Attempt <= current.ResultDeliveryAttempt)
        {
            return current;
        }

        var next = current.Clone();
        next.ResultDeliveryAttempt = evt.Attempt;
        return next;
    }

    private static NyxIdChatTurnGAgentState ApplyDelivered(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnOperationDeliveredEvent evt)
    {
        if (!KeysEqual(current.AdmittedOperation, evt.Key))
            return current;

        var next = current.Clone();
        next.ResultDelivered = true;
        next.DeliveredAt = evt.DeliveredAt?.Clone();
        next.PendingResult = null;
        if (!next.DeliveredOperations.Any(delivered => KeysEqual(delivered, evt.Key)))
            next.DeliveredOperations.Add(evt.Key.Clone());
        while (next.DeliveredOperations.Count > DeliveredOperationHistoryLimit)
            next.DeliveredOperations.RemoveAt(0);
        return next;
    }

    private bool IsValidCanaryEffectFaultTriggered(
        NyxIdChatCanaryEffectFaultTriggeredSignal signal)
    {
        var directive = State.CanaryEffectFault;
        var deniedResult = signal.DeniedResult;
        return directive?.Key is not null &&
               State.AdmittedOperation is not null &&
               !State.CanaryEffectFaultConsumed &&
               !State.ResultDelivered &&
               !IsTerminal(State.Phase) &&
               State.MayChangeExternalState &&
               State.EffectDispatchWaterline == NyxIdChatEffectEvidence.MayHaveChanged &&
               State.EffectDispatchStartedAt is not null &&
               signal.TriggeredAt is not null &&
               string.Equals(directive.ArmId, signal.ArmId, StringComparison.Ordinal) &&
               directive.Key.Equals(State.AdmittedOperation) &&
               directive.Key.Equals(deniedResult?.Key) &&
               IsCanaryApprovalObservation(
                   directive,
                   State.CanaryEffectFaultToolCallId,
                   State.CanaryEffectFaultToolName,
                   deniedResult);
    }

    private static bool IsCanaryApprovalObservation(
        NyxIdChatCanaryEffectFaultDirective directive,
        string expectedCallId,
        string expectedToolName,
        NyxIdChatOperationResultSignal? deniedResult)
    {
        var tool = deniedResult?.Tool;
        var receipt = tool?.Receipt;
        return tool?.ExternalEffect == NyxIdChatEffectEvidence.NotApplied &&
               receipt is
               {
                   Status: AgentToolReceiptStatus.Denied,
                   Effect: AgentToolReceiptEffect.Mutating,
                   NyxIdApprovalTerminalOutcome: NyxIdApprovalTerminalOutcome.Rejected,
                   ApprovalRequestId.Length: > 0,
               } &&
               receipt.NyxIdApprovalDecisionMode is
                   NyxIdApprovalDecisionMode.Unspecified or
                   NyxIdApprovalDecisionMode.PerRequest &&
               string.Equals(receipt.ErrorCode, "NYXID_APPROVAL_FAILED", StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(expectedCallId) &&
               !string.IsNullOrWhiteSpace(expectedToolName) &&
               string.Equals(receipt.CallId, expectedCallId, StringComparison.Ordinal) &&
               string.Equals(receipt.ToolName, expectedToolName, StringComparison.Ordinal) &&
               string.Equals(receipt.SubjectKind, "nyxid.user-service", StringComparison.Ordinal) &&
               string.Equals(receipt.SubjectId, directive.ServiceInstanceId, StringComparison.Ordinal);
    }

    private static bool IsValid(NyxIdChatOperationDispatchCommand command) =>
        command.Key is
        {
            ConversationActorId.Length: > 0,
            TurnId.Length: > 0,
            TaskId.Length: > 0,
            StepId.Length: > 0,
            OperationId.Length: > 0,
            OperationGeneration: > 0,
        } &&
        command.InputCase is not NyxIdChatOperationDispatchCommand.InputOneofCase.None &&
        (!NyxIdChatTurnOperationDispatchPort.MayDispatchExternalEffect(command) ||
         string.Equals(
             ResolveIdempotencyKey(command),
             command.Key.OperationId,
             StringComparison.Ordinal));

    private bool MatchesContinuationAuthorization(NyxIdChatOperationDispatchCommand command) =>
        MatchesDurableRetryAuthorization(command) &&
        MatchesExactServiceApprovalAuthorization(command);

    private bool MatchesDurableRetryAuthorization(NyxIdChatOperationDispatchCommand command)
    {
        var tool = command.Tool;
        if (tool?.RematerializeDurableAuthorization != true)
            return tool?.RetryAuthorizationSourceKey is null;

        var source = tool.RetryAuthorizationSourceKey;
        if (source is null ||
            State.AdmittedOperation is null ||
            !State.ResultDelivered ||
            !KeysEqual(State.AdmittedOperation, source) ||
            !SameTask(source, command.Key) ||
            command.Key.OperationGeneration <= 1 ||
            source.OperationGeneration != command.Key.OperationGeneration - 1 ||
            State.ExternalEffect != NyxIdChatEffectEvidence.NotApplied)
        {
            return false;
        }

        return State.OperationKind == NyxIdChatStepKind.Postcondition &&
               State.Phase == NyxIdChatOperationPhase.Succeeded ||
               State.OperationKind == NyxIdChatStepKind.Tool &&
               State.Phase is NyxIdChatOperationPhase.Failed or
                   NyxIdChatOperationPhase.Cancelled;
    }

    private bool MatchesExactServiceApprovalAuthorization(
        NyxIdChatOperationDispatchCommand command)
    {
        var continuation = command.ToolApprovalContinuation;
        var authority = continuation?.ExactServiceApproval;
        if (authority is null)
            return true;

        var source = State.AdmittedOperation;
        var admission = State.OperationAdmission;
        var approvalRequired = State.ToolReceiptStatus == AgentToolReceiptStatus.ApprovalRequired ||
                               State.ToolReceiptStatus == AgentToolReceiptStatus.Unspecified &&
                               string.IsNullOrWhiteSpace(State.TerminalCode);
        return source is not null &&
               admission?.IdentityCase ==
               AgentToolOperationAdmissionPayload.IdentityOneofCase.PublishedEndpoint &&
               admission.ExecutionPolicy?.Approval == AgentToolOperationApprovalPayload.Required &&
               State.ResultDelivered &&
               State.OperationKind == NyxIdChatStepKind.Tool &&
               State.Phase == NyxIdChatOperationPhase.Succeeded &&
               State.ExternalEffect == NyxIdChatEffectEvidence.NotStarted &&
               State.MayChangeExternalState &&
               State.ExactServiceRecoveryStage == NyxIdChatExactServiceRecoveryStage.Create &&
               approvalRequired &&
               continuation!.MayChangeExternalState &&
               SameTask(source, command.Key) &&
               string.Equals(source.StepId, command.Key.StepId, StringComparison.Ordinal) &&
               command.Key.OperationGeneration > 1 &&
               source.OperationGeneration == command.Key.OperationGeneration - 1 &&
               NyxIdChatOperationAdmissionPolicy.Matches(
                   admission,
                   continuation.OperationAdmission) &&
               !string.IsNullOrWhiteSpace(authority.RequestId) &&
               string.Equals(
                   authority.RequestId,
                   continuation.ApprovalRequestId,
                   StringComparison.Ordinal) &&
               string.Equals(authority.OperationId, source.OperationId, StringComparison.Ordinal) &&
               authority.OperationGeneration == source.OperationGeneration &&
               string.Equals(
                   authority.IdempotencyKey,
                   State.IdempotencyKey,
                   StringComparison.Ordinal) &&
               string.Equals(
                   authority.UserServiceId,
                   admission.ServiceInstanceId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   authority.EndpointId,
                   admission.PublishedEndpoint.EndpointId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   authority.CatalogDigest,
                   admission.CatalogDigest,
                   StringComparison.Ordinal) &&
               string.Equals(
                   authority.EndpointContractDigest,
                   admission.ContractDigest,
                   StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(authority.OperationDigest);
    }

    private async Task DispatchOperationDeliveryStatusAsync(
        NyxIdChatOperationKey key,
        bool admitted,
        NyxIdChatEffectEvidence effectDispatchWaterline,
        CancellationToken ct)
    {
        var signal = new NyxIdChatTurnOperationDeliveryStatusSignal
        {
            Key = key.Clone(),
            Admitted = admitted,
            EffectDispatchWaterline = effectDispatchWaterline,
        };
        var envelope = CreateDirectEnvelope(
            key.ConversationActorId,
            $"{key.OperationId}:turn-operation-delivery-status",
            signal);
        await _actorDispatchPort.DispatchAsync(
            key.ConversationActorId,
            envelope,
            ct);
    }

    private async Task TryDispatchCanaryEffectFaultConsumedAsync(CancellationToken ct)
    {
        var directive = State.CanaryEffectFault;
        if (!State.CanaryEffectFaultConsumed ||
            directive?.Key is null ||
            State.CanaryEffectFaultConsumedAt is null)
        {
            return;
        }

        var signal = new NyxIdChatCanaryEffectFaultConsumedSignal
        {
            ArmId = directive.ArmId,
            Key = directive.Key.Clone(),
            TurnActorId = Id,
            ConsumedAt = State.CanaryEffectFaultConsumedAt.Clone(),
            ServiceInstanceId = directive.ServiceInstanceId,
            ApprovalRequestId = State.CanaryEffectFaultApprovalRequestId,
            ReceiptStatus = State.CanaryEffectFaultReceiptStatus,
            ApprovalDecisionMode = State.CanaryEffectFaultApprovalDecisionMode,
            ApprovalTerminalOutcome = State.CanaryEffectFaultApprovalTerminalOutcome,
            ApprovalSubjectKind = State.CanaryEffectFaultApprovalSubjectKind,
            ApprovalSubjectId = State.CanaryEffectFaultApprovalSubjectId,
            ApprovalCallId = State.CanaryEffectFaultToolCallId,
            ApprovalToolName = State.CanaryEffectFaultToolName,
        };
        var envelope = CreateDirectEnvelope(
            directive.Key.ConversationActorId,
            $"{directive.ArmId}:canary-effect-fault-consumed",
            signal);
        try
        {
            await _actorDispatchPort.DispatchAsync(
                directive.Key.ConversationActorId,
                envelope,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat canary consumption acknowledgement failed: turnActor={TurnActorId} operation={OperationId}",
                Id,
                directive.Key.OperationId);
            await TryScheduleCanaryEffectFaultConsumedRetryAsync(directive);
        }
    }

    private async Task TryScheduleCanaryEffectFaultConsumedRetryAsync(
        NyxIdChatCanaryEffectFaultDirective directive)
    {
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                $"{directive.ArmId}:canary-effect-fault-consumed-retry",
                CanaryEffectFaultConsumedRetryDelay,
                new NyxIdChatCanaryEffectFaultConsumedRetryRequested
                {
                    ArmId = directive.ArmId,
                    Key = directive.Key.Clone(),
                },
                new EventEnvelopePublishOptions
                {
                    Propagation = new EventEnvelopePropagationOverrides
                    {
                        CorrelationId = directive.Key.OperationId,
                    },
                    Delivery = new EventEnvelopeDeliveryOptions
                    {
                        OperationId = $"{directive.ArmId}:canary-effect-fault-consumed-retry",
                    },
                },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat canary consumption acknowledgement retry could not be scheduled: turnActor={TurnActorId} operation={OperationId}",
                Id,
                directive.Key.OperationId);
        }
    }

    private bool IsValidOperationDeliveryProbe(NyxIdChatOperationKey? key) =>
        key is not null &&
        !string.IsNullOrWhiteSpace(key.ConversationActorId) &&
        !string.IsNullOrWhiteSpace(key.TurnId) &&
        !string.IsNullOrWhiteSpace(key.TaskId) &&
        !string.IsNullOrWhiteSpace(key.StepId) &&
        !string.IsNullOrWhiteSpace(key.OperationId) &&
        key.OperationGeneration > 0 &&
        string.Equals(
            NyxIdChatTurnActorIds.ForTurn(key.ConversationActorId, key.TurnId),
            Id,
            StringComparison.Ordinal);

    private static string ResolveIdempotencyKey(NyxIdChatOperationDispatchCommand command) =>
        command.Tool?.IdempotencyKey ??
        command.ToolApprovalContinuation?.IdempotencyKey ??
        string.Empty;

    private async Task<RecoveryAdmission> PrepareRecoveryAdmissionAsync(
        NyxIdChatOperationDispatchCommand command,
        Aevatar.AI.Abstractions.ToolProviders.AgentToolOperationAdmissionPayload? operationAdmission,
        AgentToolExecutionContextPayload? capturedToolContext)
    {
        var readBack = command.ToolVerification?.ReadBack?.Clone() ??
                       operationAdmission?.ReadBack?.Clone();
        var effectStepId = command.ToolVerification?.EffectStepId ?? command.Key.StepId;
        var exactServiceStage = ResolveExactServiceRecoveryStage(command, operationAdmission);
        if (!NyxIdChatOperationAdmissionPolicy.IsValidReadBack(readBack) &&
            exactServiceStage == NyxIdChatExactServiceRecoveryStage.Unspecified)
        {
            return new RecoveryAdmission(
                null,
                null,
                null,
                effectStepId,
                exactServiceStage);
        }

        if (command.ToolVerification is not null &&
            State.RecoveryContext is not null &&
            State.RecoveryCredential is not null)
        {
            return new RecoveryAdmission(
                State.RecoveryContext.Clone(),
                State.RecoveryCredential.Clone(),
                readBack,
                effectStepId,
                State.ExactServiceRecoveryStage);
        }

        var toolContext = command.Tool?.ToolContext ??
                          command.ToolApprovalContinuation?.ToolContext ??
                          capturedToolContext;
        if (_secretVault is null || toolContext?.Credentials is null)
        {
            return new RecoveryAdmission(
                null,
                null,
                readBack,
                effectStepId,
                exactServiceStage);
        }

        var context = Aevatar.AI.Abstractions.ToolProviders.AgentToolExecutionContextMapper
            .FromPayload(toolContext);
        var ownerSubject = context.Caller.OwnerSubject?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ownerSubject) || !HasCredential(toolContext.Credentials))
            return new RecoveryAdmission(
                AgentToolExecutionContextMapper.ToRecoveryPayload(context),
                null,
                readBack,
                effectStepId,
                exactServiceStage);

        var ownerScopeKey = $"nyxid-chat:{command.Key.ConversationActorId}";
        try
        {
            var secret = exactServiceStage == NyxIdChatExactServiceRecoveryStage.Unspecified
                ? Convert.ToBase64String(toolContext.Credentials.ToByteArray())
                : NyxIdChatRecoverySecretPayloadCodec.Encode(
                    toolContext.Credentials,
                    BuildExactServiceRecoveryCommand(command));
            var stored = await _secretVault.PutAsync(new StoreSecretRequest(
                CredentialSecretPurposes.NyxIdChatRecoveryCredential,
                ownerScopeKey,
                ownerSubject,
                secret,
                "nyxid chat effect read-back recovery",
                _timeProvider.GetUtcNow() + RecoveryCredentialRetention));
            return new RecoveryAdmission(
                AgentToolExecutionContextMapper.ToRecoveryPayload(context),
                new DurableCallerCredentialRef
                {
                    Ref = stored.Reference.Ref,
                    Purpose = CredentialSecretPurposes.NyxIdChatRecoveryCredential,
                    OwnerScopeKey = ownerScopeKey,
                    SubjectId = ownerSubject,
                    SourceKind = DurableCallerCredentialSourceKind.NyxIdChat,
                },
                readBack,
                effectStepId,
                exactServiceStage);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                exception,
                "NyxIdChat recovery credential storage failed: turnActor={TurnActorId} operation={OperationId}",
                Id,
                command.Key.OperationId);
            return new RecoveryAdmission(
                AgentToolExecutionContextMapper.ToRecoveryPayload(context),
                null,
                readBack,
                effectStepId,
                exactServiceStage);
        }
    }

    private static NyxIdChatExactServiceRecoveryStage ResolveExactServiceRecoveryStage(
        NyxIdChatOperationDispatchCommand command,
        AgentToolOperationAdmissionPayload? operationAdmission)
    {
        if (command.ToolApprovalContinuation?.ExactServiceApproval is not null)
            return NyxIdChatExactServiceRecoveryStage.DecideRedeem;

        return command.Tool is { MayChangeExternalState: true } &&
               operationAdmission?.IdentityCase ==
               AgentToolOperationAdmissionPayload.IdentityOneofCase.PublishedEndpoint
            ? NyxIdChatExactServiceRecoveryStage.Create
            : NyxIdChatExactServiceRecoveryStage.Unspecified;
    }

    private static NyxIdChatOperationDispatchCommand BuildExactServiceRecoveryCommand(
        NyxIdChatOperationDispatchCommand command)
    {
        var frozen = command.Clone();
        if (frozen.Tool is not null)
        {
            frozen.Tool.ToolContext = null;
        }
        if (frozen.ToolApprovalContinuation is not null)
            frozen.ToolApprovalContinuation.ToolContext = null;
        return frozen;
    }

    private static bool HasCredential(Aevatar.AI.Abstractions.AgentToolCredentialsPayload credentials) =>
        !string.IsNullOrWhiteSpace(credentials.NyxIdAccessToken) ||
        !string.IsNullOrWhiteSpace(credentials.NyxIdOrgToken) ||
        !string.IsNullOrWhiteSpace(credentials.SenderNyxIdAccessToken) ||
        !string.IsNullOrWhiteSpace(credentials.SourceReadableNyxIdAccessToken);

    private sealed record RecoveryAdmission(
        Aevatar.AI.Abstractions.AgentToolRecoveryContextPayload? Context,
        DurableCallerCredentialRef? Credential,
        Aevatar.AI.Abstractions.ToolProviders.AgentToolOperationReadBackPayload? ReadBack,
        string EffectStepId,
        NyxIdChatExactServiceRecoveryStage ExactServiceStage);

    private static Aevatar.AI.Abstractions.ToolProviders.AgentToolOperationAdmissionPayload?
        ResolveOperationAdmission(NyxIdChatOperationDispatchCommand command) =>
        command.Tool?.OperationAdmission ??
        command.ToolApprovalContinuation?.OperationAdmission ??
        command.ToolVerification?.ReadBack?.ReadOperation;

    private static NyxIdChatStepKind ResolveKind(NyxIdChatOperationDispatchCommand command) =>
        command.InputCase switch
        {
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm => NyxIdChatStepKind.Llm,
            NyxIdChatOperationDispatchCommand.InputOneofCase.InputContinuation => NyxIdChatStepKind.Llm,
            NyxIdChatOperationDispatchCommand.InputOneofCase.ConditionContinuation =>
                NyxIdChatStepKind.Llm,
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool => NyxIdChatStepKind.Tool,
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolApprovalContinuation =>
                NyxIdChatStepKind.Tool,
            NyxIdChatOperationDispatchCommand.InputOneofCase.ActionPostcondition =>
                NyxIdChatStepKind.Postcondition,
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolVerification =>
                NyxIdChatStepKind.Postcondition,
            _ => NyxIdChatStepKind.Unspecified,
        };

    private static bool SameTurn(NyxIdChatOperationKey left, NyxIdChatOperationKey right) =>
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal);

    private static bool SameTask(NyxIdChatOperationKey left, NyxIdChatOperationKey right) =>
        string.Equals(left.ConversationActorId, right.ConversationActorId, StringComparison.Ordinal) &&
        string.Equals(left.TurnId, right.TurnId, StringComparison.Ordinal) &&
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal);

    private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
        left is not null &&
        right is not null &&
        SameTurn(left, right) &&
        string.Equals(left.StepId, right.StepId, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration;

    private static bool IsTerminal(NyxIdChatOperationPhase phase) =>
        phase is NyxIdChatOperationPhase.Succeeded or
            NyxIdChatOperationPhase.Failed or
            NyxIdChatOperationPhase.Cancelled or
            NyxIdChatOperationPhase.Uncertain;

    private static NyxIdChatEffectEvidence NormalizeEffect(
        NyxIdChatEffectEvidence value,
        NyxIdChatEffectEvidence fallback) =>
        value == NyxIdChatEffectEvidence.Unspecified ? fallback : value;

    private static bool EffectMayHaveChanged(NyxIdChatTurnGAgentState state) =>
        state.MayChangeExternalState &&
        state.EffectDispatchWaterline != NyxIdChatEffectEvidence.NotStarted;

    private long CurrentCommittedVersion() =>
        (EventSourcing ?? throw new InvalidOperationException(
            "Event sourcing must be configured before recovery."))
        .CurrentVersion;

    private sealed record OperationCompletion(
        NyxIdChatOperationPhase Phase,
        string TerminalCode,
        string SafeMessage,
        NyxIdChatEffectEvidence ExternalEffect);
}
