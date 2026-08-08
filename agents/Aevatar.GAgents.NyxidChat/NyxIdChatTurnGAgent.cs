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
    internal const string PlanGateCapabilityExpiredCode =
        "NYXID_CHAT_PLAN_GATE_CAPABILITY_EXPIRED";
    private const string PlanGateCapabilityExpiredMessage =
        "The admitted plan can no longer execute after recovery. Re-plan from a safe checkpoint.";

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
            .On<NyxIdChatTurnPlanGateAdmissionCommittedEvent>(ApplyPlanGateAdmissionCommitted)
            .On<NyxIdChatTurnPlanGateAdmissionExpiredEvent>(ApplyPlanGateAdmissionExpired)
            .On<NyxIdChatTurnPlanGateAdmissionRevokedEvent>(ApplyPlanGateAdmissionRevoked)
            .On<NyxIdChatTurnOperationDeliveryFencedEvent>(ApplyOperationDeliveryFenced)
            .On<NyxIdChatTurnEffectDispatchStartedEvent>(ApplyEffectDispatchStarted)
            .On<NyxIdChatTurnOperationReconciliationStartedEvent>(ApplyReconciliationStarted)
            .On<NyxIdChatTurnOperationCompletedEvent>(ApplyCompleted)
            .On<NyxIdChatTurnOperationDeliveredEvent>(ApplyDelivered)
            .OrCurrent();

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        if (State.PlanGateAdmission is
            {
                RematerializeDurableAuthorization: false,
            } staleAdmission)
        {
            await DispatchPlanGateCapabilityExpiredAsync(staleAdmission, ct);
            await PersistDomainEventAsync(new NyxIdChatTurnPlanGateAdmissionExpiredEvent
            {
                Admission = staleAdmission.Clone(),
                ExpiredAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, ct);
        }

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
    public async Task HandlePlanGateAdmissionAsync(
        NyxIdChatTurnPlanGateAdmissionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsValidPlanGateAdmission(command))
            return;

        if (State.RevokedPlanGateAdmissions.Any(revoked =>
                revoked.Equals(command.Admission)))
            return;

        if (State.PlanGateAdmission is { } existing)
        {
            if (existing.Equals(command.Admission))
                await DispatchPlanGateAdmissionCommittedAsync(existing, CancellationToken.None);

            return;
        }

        await PersistDomainEventAsync(new NyxIdChatTurnPlanGateAdmissionCommittedEvent
        {
            Admission = command.Admission.Clone(),
        }, CancellationToken.None);

        await DispatchPlanGateAdmissionCommittedAsync(
            command.Admission,
            CancellationToken.None);
    }

    [EventHandler]
    public async Task HandlePlanGateAdmissionRevokeAsync(
        NyxIdChatTurnPlanGateAdmissionRevokeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!CanRevokePlanGateAdmission(command))
            return;

        if (!State.RevokedPlanGateAdmissions.Any(revoked =>
                revoked.Equals(command.Admission)))
        {
            await PersistDomainEventAsync(new NyxIdChatTurnPlanGateAdmissionRevokedEvent
            {
                Admission = command.Admission.Clone(),
                ReasonCode = command.ReasonCode,
                RevokedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, CancellationToken.None);
        }

        var signal = new NyxIdChatTurnPlanGateAdmissionRevokedSignal
        {
            Admission = command.Admission.Clone(),
            ReasonCode = command.ReasonCode,
        };
        var envelope = CreateDirectEnvelope(
            command.Admission.Key.ConversationActorId,
            $"{command.Admission.GateRequestId}:turn-admission-revoked",
            signal);
        await _actorDispatchPort.DispatchAsync(
            command.Admission.Key.ConversationActorId,
            envelope,
            CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleOperationAsync(NyxIdChatOperationDispatchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsValid(command))
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

        if (State.FencedOperationDeliveries.Any(key => KeysEqual(key, command.Key)))
        {
            await DispatchOperationDeliveryStatusAsync(
                command.Key,
                admitted: false,
                NyxIdChatEffectEvidence.NotStarted,
                CancellationToken.None);
            return;
        }

        if (!MatchesPlanGateAdmission(command) || IsDuplicateOrUnavailable(command))
            return;

        var kind = ResolveKind(command);
        var admittedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var mayChangeExternalState = command.Tool?.MayChangeExternalState == true ||
                                     command.ToolApprovalContinuation?.MayChangeExternalState == true ||
                                     command.PlanGateContinuation?.MayChangeExternalState == true;
        var idempotent = command.Tool?.Idempotent == true;
        var idempotencyKey = ResolveIdempotencyKey(command);
        var operationAdmission = ResolveOperationAdmission(command);
        await HydrateFrozenVerificationContextAsync(command);
        var recovery = await PrepareRecoveryAdmissionAsync(command, operationAdmission);
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
            ConsumesPlanGateAdmission = command.PlanGateContinuation is not null,
            AdmittedAt = admittedAt,
        };
        if (recovery.Context is not null)
            admitted.RecoveryContext = recovery.Context;
        if (recovery.Credential is not null)
            admitted.RecoveryCredential = recovery.Credential;
        if (recovery.ReadBack is not null)
            admitted.RecoveryReadBack = recovery.ReadBack;
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
            await PersistDomainEventAsync(new NyxIdChatTurnEffectDispatchStartedEvent
            {
                Key = command.Key.Clone(),
                StartedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            }, CancellationToken.None);
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

        var completedUndelivered = State.CompletedAt is not null && IsTerminal(State.Phase);
        if (!completedUndelivered &&
            (EffectMayHaveChanged(State) ||
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
        if (!resolved.Resolved || !TryParseRecoveryCredentials(resolved.Secret, out var credentials) ||
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
                    Convert.ToBase64String(credentials.ToByteArray()),
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
            !TryParseRecoveryCredentials(resolved.Secret, out var credentials) ||
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

    private static bool TryParseRecoveryCredentials(
        string? encoded,
        out AgentToolCredentialsPayload credentials)
    {
        try
        {
            credentials = AgentToolCredentialsPayload.Parser.ParseFrom(
                Convert.FromBase64String(encoded ?? string.Empty));
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidProtocolBufferException)
        {
            credentials = new AgentToolCredentialsPayload();
            return false;
        }
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
        await PersistDomainEventAsync(new NyxIdChatTurnOperationCompletedEvent
        {
            Key = result.Key.Clone(),
            Phase = completion.Phase,
            TerminalCode = completion.TerminalCode,
            SafeMessage = completion.SafeMessage,
            ExternalEffect = completion.ExternalEffect,
            CompletedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            ProviderResourceId = result.Tool?.Receipt?.ProviderResourceId ?? string.Empty,
        }, CancellationToken.None);
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
        await PersistDomainEventAsync(new NyxIdChatTurnOperationDeliveredEvent
        {
            Key = result.Key.Clone(),
            DeliveredAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        }, CancellationToken.None);
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
        if (!resolved.Resolved || !TryParseRecoveryCredentials(resolved.Secret, out var credentials))
            return;

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
        if (evt.ConsumesPlanGateAdmission)
            next.PlanGateAdmission = null;
        next.ReconciliationStartedAt = null;
        next.ResultDelivered = false;
        next.AdmittedAt = evt.AdmittedAt?.Clone();
        next.CompletedAt = null;
        next.DeliveredAt = null;
        return next;
    }

    private static NyxIdChatTurnGAgentState ApplyPlanGateAdmissionCommitted(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnPlanGateAdmissionCommittedEvent evt)
    {
        if (evt.Admission is null)
            return current;

        var next = current.Clone();
        next.PlanGateAdmission = evt.Admission.Clone();
        return next;
    }

    private static NyxIdChatTurnGAgentState ApplyPlanGateAdmissionExpired(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnPlanGateAdmissionExpiredEvent evt)
    {
        if (current.PlanGateAdmission is null ||
            evt.Admission is null ||
            !current.PlanGateAdmission.Equals(evt.Admission))
        {
            return current;
        }

        var next = current.Clone();
        next.PlanGateAdmission = null;
        return next;
    }

    private static NyxIdChatTurnGAgentState ApplyPlanGateAdmissionRevoked(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnPlanGateAdmissionRevokedEvent evt)
    {
        if (evt.Admission is null ||
            (current.PlanGateAdmission is { } active &&
             !active.Equals(evt.Admission)))
        {
            return current;
        }

        if (current.RevokedPlanGateAdmissions.Any(revoked =>
                revoked.Equals(evt.Admission)))
        {
            return current;
        }

        var next = current.Clone();
        if (next.PlanGateAdmission?.Equals(evt.Admission) == true)
            next.PlanGateAdmission = null;
        next.RevokedPlanGateAdmissions.Add(evt.Admission.Clone());
        while (next.RevokedPlanGateAdmissions.Count > DeliveredOperationHistoryLimit)
            next.RevokedPlanGateAdmissions.RemoveAt(0);
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

    private static NyxIdChatTurnGAgentState ApplyReconciliationStarted(
        NyxIdChatTurnGAgentState current,
        NyxIdChatTurnOperationReconciliationStartedEvent evt)
    {
        if (!KeysEqual(current.AdmittedOperation, evt.Key) ||
            !EffectMayHaveChanged(current))
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
        next.CompletedAt = evt.CompletedAt?.Clone();
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
        if (!next.DeliveredOperations.Any(delivered => KeysEqual(delivered, evt.Key)))
            next.DeliveredOperations.Add(evt.Key.Clone());
        while (next.DeliveredOperations.Count > DeliveredOperationHistoryLimit)
            next.DeliveredOperations.RemoveAt(0);
        return next;
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

    private bool IsValidPlanGateAdmission(NyxIdChatTurnPlanGateAdmissionCommand command)
    {
        var admission = command.Admission;
        if (admission?.Key is not
            {
                ConversationActorId.Length: > 0,
                TurnId.Length: > 0,
                TaskId.Length: > 0,
                StepId.Length: > 0,
                OperationId.Length: > 0,
                OperationGeneration: > 0,
            } ||
            command.SourceOperationKey is null ||
            State.AdmittedOperation is null ||
            !State.ResultDelivered ||
            !KeysEqual(State.AdmittedOperation, command.SourceOperationKey) ||
            !SameTask(command.SourceOperationKey, admission.Key) ||
            !string.Equals(admission.TaskId, admission.Key.TaskId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(admission.GateRequestId) ||
            string.IsNullOrWhiteSpace(admission.PlanId) ||
            admission.PlanRevision <= 0 ||
            string.IsNullOrWhiteSpace(admission.ToolCallId) ||
            string.IsNullOrWhiteSpace(admission.ToolName) ||
            admission.ArgumentsSha256.Length != SHA256.HashSizeInBytes)
        {
            return false;
        }

        if (!admission.RematerializeDurableAuthorization)
        {
            return State.OperationKind == NyxIdChatStepKind.Llm &&
                   State.Phase == NyxIdChatOperationPhase.Succeeded;
        }

        var sourceProvesNotApplied =
            State.OperationKind == NyxIdChatStepKind.Postcondition &&
            State.Phase == NyxIdChatOperationPhase.Succeeded ||
            State.OperationKind == NyxIdChatStepKind.Tool &&
            State.Phase is NyxIdChatOperationPhase.Failed or
                NyxIdChatOperationPhase.Cancelled;
        return sourceProvesNotApplied &&
               State.ExternalEffect == NyxIdChatEffectEvidence.NotApplied &&
               admission.Key.OperationGeneration > 1 &&
               command.SourceOperationKey.OperationGeneration ==
               admission.Key.OperationGeneration - 1;
    }

    private bool CanRevokePlanGateAdmission(
        NyxIdChatTurnPlanGateAdmissionRevokeCommand command)
    {
        var admission = command.Admission;
        if (admission?.Key is null ||
            string.IsNullOrWhiteSpace(command.ReasonCode))
        {
            return false;
        }

        if (State.RevokedPlanGateAdmissions.Any(revoked => revoked.Equals(admission)))
            return true;

        if (State.PlanGateAdmission is { } active)
            return active.Equals(admission);

        if (KeysEqual(State.AdmittedOperation, admission.Key))
            return true;

        if (State.AdmittedOperation is null)
            return false;

        return IsValidPlanGateAdmission(new NyxIdChatTurnPlanGateAdmissionCommand
        {
            SourceOperationKey = State.AdmittedOperation.Clone(),
            Admission = admission.Clone(),
        });
    }

    private bool MatchesPlanGateAdmission(NyxIdChatOperationDispatchCommand command)
    {
        if (command.PlanGateContinuation is not { } continuation)
            return State.PlanGateAdmission is null &&
                   MatchesDurableRetryAuthorization(command);

        var expected = State.PlanGateAdmission;
        return expected?.Key is not null &&
               KeysEqual(expected.Key, command.Key) &&
               string.Equals(expected.GateRequestId, continuation.GateRequestId, StringComparison.Ordinal) &&
               string.Equals(expected.TaskId, continuation.TaskId, StringComparison.Ordinal) &&
               string.Equals(expected.PlanId, continuation.PlanId, StringComparison.Ordinal) &&
               expected.PlanRevision == continuation.PlanRevision &&
               string.Equals(expected.ToolCallId, continuation.ToolCallId, StringComparison.Ordinal) &&
               string.Equals(expected.ToolName, continuation.ToolName, StringComparison.Ordinal) &&
               expected.MayChangeExternalState == continuation.MayChangeExternalState &&
               expected.RematerializeDurableAuthorization ==
               continuation.RematerializeDurableAuthorization &&
               expected.ArgumentsSha256.Length == continuation.ArgumentsSha256.Length &&
               CryptographicOperations.FixedTimeEquals(
                   expected.ArgumentsSha256.Span,
                   continuation.ArgumentsSha256.Span) &&
               NyxIdChatOperationAdmissionPolicy.Matches(
                   expected.OperationAdmission,
                   continuation.OperationAdmission);
    }

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

    private async Task DispatchPlanGateAdmissionCommittedAsync(
        NyxIdChatTurnPlanGateAdmissionState admission,
        CancellationToken ct)
    {
        var signal = new NyxIdChatTurnPlanGateAdmissionCommittedSignal
        {
            Admission = admission.Clone(),
        };
        var envelope = CreateDirectEnvelope(
            admission.Key.ConversationActorId,
            $"{admission.GateRequestId}:turn-admission-committed",
            signal);
        await _actorDispatchPort.DispatchAsync(
            admission.Key.ConversationActorId,
            envelope,
            ct);
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

    private async Task DispatchPlanGateCapabilityExpiredAsync(
        NyxIdChatTurnPlanGateAdmissionState admission,
        CancellationToken ct)
    {
        var signal = new NyxIdChatPlanGateCapabilityExpiredSignal
        {
            Admission = admission.Clone(),
            FailureCode = PlanGateCapabilityExpiredCode,
            SafeMessage = PlanGateCapabilityExpiredMessage,
        };
        var envelope = CreateDirectEnvelope(
            admission.Key.ConversationActorId,
            $"{admission.GateRequestId}:capability-expired",
            signal);
        await _actorDispatchPort
            .DispatchAsync(admission.Key.ConversationActorId, envelope, ct);
    }

    private static string ResolveIdempotencyKey(NyxIdChatOperationDispatchCommand command) =>
        command.Tool?.IdempotencyKey ??
        command.ToolApprovalContinuation?.IdempotencyKey ??
        command.PlanGateContinuation?.IdempotencyKey ??
        string.Empty;

    private async Task<RecoveryAdmission> PrepareRecoveryAdmissionAsync(
        NyxIdChatOperationDispatchCommand command,
        Aevatar.AI.Abstractions.ToolProviders.AgentToolOperationAdmissionPayload? operationAdmission)
    {
        var readBack = command.ToolVerification?.ReadBack?.Clone() ??
                       operationAdmission?.ReadBack?.Clone();
        var effectStepId = command.ToolVerification?.EffectStepId ?? command.Key.StepId;
        if (!NyxIdChatOperationAdmissionPolicy.IsValidReadBack(readBack))
            return new RecoveryAdmission(null, null, null, effectStepId);

        if (command.ToolVerification is not null &&
            State.RecoveryContext is not null &&
            State.RecoveryCredential is not null)
        {
            return new RecoveryAdmission(
                State.RecoveryContext.Clone(),
                State.RecoveryCredential.Clone(),
                readBack,
                effectStepId);
        }

        var toolContext = command.Tool?.ToolContext ??
                          command.ToolApprovalContinuation?.ToolContext ??
                          command.PlanGateContinuation?.ToolContext;
        if (_secretVault is null || toolContext?.Credentials is null)
            return new RecoveryAdmission(null, null, readBack, effectStepId);

        var context = Aevatar.AI.Abstractions.ToolProviders.AgentToolExecutionContextMapper
            .FromPayload(toolContext);
        var ownerSubject = context.Caller.OwnerSubject?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ownerSubject) || !HasCredential(toolContext.Credentials))
            return new RecoveryAdmission(
                AgentToolExecutionContextMapper.ToRecoveryPayload(context),
                null,
                readBack,
                effectStepId);

        var ownerScopeKey = $"nyxid-chat:{command.Key.ConversationActorId}";
        try
        {
            var stored = await _secretVault.PutAsync(new StoreSecretRequest(
                CredentialSecretPurposes.NyxIdChatRecoveryCredential,
                ownerScopeKey,
                ownerSubject,
                Convert.ToBase64String(toolContext.Credentials.ToByteArray()),
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
                effectStepId);
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
                effectStepId);
        }
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
        string EffectStepId);

    private static Aevatar.AI.Abstractions.ToolProviders.AgentToolOperationAdmissionPayload?
        ResolveOperationAdmission(NyxIdChatOperationDispatchCommand command) =>
        command.Tool?.OperationAdmission ??
        command.ToolApprovalContinuation?.OperationAdmission ??
        command.PlanGateContinuation?.OperationAdmission ??
        command.ToolVerification?.ReadBack?.ReadOperation;

    private static NyxIdChatStepKind ResolveKind(NyxIdChatOperationDispatchCommand command) =>
        command.InputCase switch
        {
            NyxIdChatOperationDispatchCommand.InputOneofCase.Llm => NyxIdChatStepKind.Llm,
            NyxIdChatOperationDispatchCommand.InputOneofCase.InputContinuation => NyxIdChatStepKind.Llm,
            NyxIdChatOperationDispatchCommand.InputOneofCase.Tool => NyxIdChatStepKind.Tool,
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolApprovalContinuation =>
                NyxIdChatStepKind.Tool,
            NyxIdChatOperationDispatchCommand.InputOneofCase.PlanGateContinuation =>
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
