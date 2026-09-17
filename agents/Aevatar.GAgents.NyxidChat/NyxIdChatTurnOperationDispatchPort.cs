using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ExactServiceApprovals;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aevatar.GAgents.NyxidChat;

public interface INyxIdChatTurnOperationDispatchPort
{
    INyxIdChatTurnOperationDispatchSession OpenSession();
}

public interface INyxIdChatTurnOperationDispatchSession
{
    AgentToolExecutionContextPayload? CaptureToolContext() => null;

    Task DispatchExecutionAsync(
        string turnActorId,
        NyxIdChatOperationDispatchCommand command,
        string correlationId,
        CancellationToken ct);

    Task DispatchReconciliationAsync(
        string turnActorId,
        NyxIdChatTurnOperationReconciliationInput input,
        string correlationId,
        CancellationToken ct);

    Task CancelExecutionAsync(NyxIdChatOperationKey key, CancellationToken ct);
}

public interface INyxIdChatTurnOperationReconciliationPort
{
    Task<NyxIdChatOperationResultSignal> ReconcileAsync(
        NyxIdChatTurnOperationReconciliationInput input,
        CancellationToken ct);
}

public sealed class UnavailableNyxIdChatTurnOperationReconciliationPort
    : INyxIdChatTurnOperationReconciliationPort
{
    internal const string OutcomeUncertainCode = "NYXID_CHAT_OPERATION_OUTCOME_UNCERTAIN";
    private const string OutcomeUncertainMessage =
        "The external operation may have changed state and could not be reconciled.";

    public Task<NyxIdChatOperationResultSignal> ReconcileAsync(
        NyxIdChatTurnOperationReconciliationInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new NyxIdChatOperationResultSignal
        {
            Key = input.Key?.Clone(),
            Failure = new NyxIdChatOperationFailure
            {
                FailureCode = OutcomeUncertainCode,
                SafeMessage = OutcomeUncertainMessage,
                ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
            },
        });
    }
}

/// <summary>
/// Production recovery path. It resumes a frozen exact-service admission or
/// executes the Tier-B read operation; missing credentials or proof stay
/// honest uncertainty and never cause the provider effect to be dispatched again.
/// </summary>
public sealed class AdmittedNyxIdChatTurnOperationReconciliationPort
    : INyxIdChatTurnOperationReconciliationPort
{
    private readonly INyxIdChatToolVerificationPort _verificationPort;
    private readonly ISecretVault? _secretVault;
    private readonly INyxIdChatDelegationCredentialLifecyclePort _delegationCredentialLifecycle;
    private readonly INyxIdExactServiceApprovalPort _exactServiceApprovalPort;
    private readonly ILogger<AdmittedNyxIdChatTurnOperationReconciliationPort> _logger;

    public AdmittedNyxIdChatTurnOperationReconciliationPort(
        INyxIdChatToolVerificationPort verificationPort)
        : this(
            verificationPort,
            null,
            new NyxIdChatDelegationCredentialLifecyclePort(TimeProvider.System),
            new UnavailableNyxIdExactServiceApprovalPort(),
            NullLogger<AdmittedNyxIdChatTurnOperationReconciliationPort>.Instance)
    {
    }

    public AdmittedNyxIdChatTurnOperationReconciliationPort(
        INyxIdChatToolVerificationPort verificationPort,
        ISecretVault? secretVault,
        INyxIdChatDelegationCredentialLifecyclePort delegationCredentialLifecycle)
        : this(
            verificationPort,
            secretVault,
            delegationCredentialLifecycle,
            new UnavailableNyxIdExactServiceApprovalPort(),
            NullLogger<AdmittedNyxIdChatTurnOperationReconciliationPort>.Instance)
    {
    }

    public AdmittedNyxIdChatTurnOperationReconciliationPort(
        INyxIdChatToolVerificationPort verificationPort,
        ISecretVault? secretVault,
        INyxIdChatDelegationCredentialLifecyclePort delegationCredentialLifecycle,
        ILogger<AdmittedNyxIdChatTurnOperationReconciliationPort> logger)
        : this(
            verificationPort,
            secretVault,
            delegationCredentialLifecycle,
            new UnavailableNyxIdExactServiceApprovalPort(),
            logger)
    {
    }

    public AdmittedNyxIdChatTurnOperationReconciliationPort(
        INyxIdChatToolVerificationPort verificationPort,
        ISecretVault? secretVault,
        INyxIdChatDelegationCredentialLifecyclePort delegationCredentialLifecycle,
        INyxIdExactServiceApprovalPort exactServiceApprovalPort,
        ILogger<AdmittedNyxIdChatTurnOperationReconciliationPort> logger)
    {
        _verificationPort = verificationPort;
        _secretVault = secretVault;
        _delegationCredentialLifecycle = delegationCredentialLifecycle;
        _exactServiceApprovalPort = exactServiceApprovalPort ??
                                    throw new ArgumentNullException(nameof(exactServiceApprovalPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NyxIdChatOperationResultSignal> ReconcileAsync(
        NyxIdChatTurnOperationReconciliationInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Key is null ||
            (input.ExactServiceRecoveryStage ==
             NyxIdChatExactServiceRecoveryStage.Unspecified &&
             !NyxIdChatOperationAdmissionPolicy.IsValidReadBack(input.ReadBack)) ||
            !IsValidCredentialReference(input))
            return Uncertain(input.Key);

        var resolved = await _secretVault!.ResolveAsync(new ResolveSecretRequest(
            input.RecoveryCredential.Ref,
            CredentialSecretPurposes.NyxIdChatRecoveryCredential,
            input.RecoveryCredential.OwnerScopeKey,
            input.RecoveryCredential.SubjectId,
            "nyxid chat effect read-back recovery"), ct).ConfigureAwait(false);
        if (!resolved.Resolved ||
            !NyxIdChatRecoverySecretPayloadCodec.TryDecode(resolved.Secret, out var recoverySecret))
            return Uncertain(input.Key);

        var credentials = recoverySecret.Credentials;

        if (credentials.NyxIdCredentialKind == AgentToolNyxIdCredentialKindPayload.ProxyDelegation)
        {
            var delegation = await _delegationCredentialLifecycle
                .ResolveAsync(credentials.NyxIdAccessToken, ct)
                .ConfigureAwait(false);
            if (!delegation.Succeeded || string.IsNullOrWhiteSpace(delegation.AccessToken))
                return Uncertain(input.Key);

            credentials.NyxIdAccessToken = delegation.AccessToken;
            if (delegation.Refreshed)
            {
                try
                {
                    await _secretVault.RotateAsync(new RotateSecretRequest(
                        input.RecoveryCredential.Ref,
                        CredentialSecretPurposes.NyxIdChatRecoveryCredential,
                        input.RecoveryCredential.OwnerScopeKey,
                        input.RecoveryCredential.SubjectId,
                        NyxIdChatRecoverySecretPayloadCodec.Encode(recoverySecret, credentials),
                        "rotate refreshed nyxid chat recovery credential"), ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        exception,
                        "NyxIdChat recovery credential rotation failed after delegation refresh: operation={OperationId}",
                        input.Key.OperationId);
                }
            }
        }

        if (input.ExactServiceRecoveryStage !=
            NyxIdChatExactServiceRecoveryStage.Unspecified)
        {
            var result = await ReconcileExactServiceAsync(
                    input,
                    recoverySecret.ExactServiceCommand,
                    credentials,
                    ct)
                .ConfigureAwait(false);
            if (!IsOutcomeUncertain(result))
                await RevokeRecoveryCredentialAsync(input, ct).ConfigureAwait(false);
            return result;
        }

        var context = AgentToolExecutionContextMapper.FromRecoveryPayload(input.RecoveryContext) with
        {
            Credentials = AgentToolExecutionContextMapper.FromPayload(
                new AgentToolExecutionContextPayload { Credentials = credentials }).Credentials,
        };

        var verification = await _verificationPort.VerifyAsync(
            input.Key,
            new NyxIdChatToolVerificationInput
            {
                EffectStepId = string.IsNullOrWhiteSpace(input.EffectStepId)
                    ? input.Key.StepId
                    : input.EffectStepId,
                ReadBack = input.ReadBack.Clone(),
                ProviderResourceId = input.ProviderResourceId,
                ToolContext = context.ToPayload(),
            },
            ct).ConfigureAwait(false);
        if (verification.Disposition is
            NyxIdChatToolVerificationDisposition.Applied or
            NyxIdChatToolVerificationDisposition.NotApplied)
        {
            await RevokeRecoveryCredentialAsync(input, ct).ConfigureAwait(false);
        }
        return new NyxIdChatOperationResultSignal
        {
            Key = input.Key.Clone(),
            ToolVerification = verification,
        };
    }

    private async Task<NyxIdChatOperationResultSignal> ReconcileExactServiceAsync(
        NyxIdChatTurnOperationReconciliationInput input,
        NyxIdChatOperationDispatchCommand? command,
        AgentToolCredentialsPayload credentials,
        CancellationToken ct)
    {
        if (command?.Key is null ||
            !command.Key.Equals(input.Key) ||
            string.IsNullOrWhiteSpace(credentials.NyxIdAccessToken))
        {
            return Uncertain(input.Key);
        }

        return input.ExactServiceRecoveryStage switch
        {
            NyxIdChatExactServiceRecoveryStage.Create =>
                await ReconcileExactServiceCreateAsync(input.Key, command, credentials, ct)
                    .ConfigureAwait(false),
            NyxIdChatExactServiceRecoveryStage.DecideRedeem =>
                await ReconcileExactServiceDecisionAsync(input.Key, command, credentials, ct)
                    .ConfigureAwait(false),
            _ => Uncertain(input.Key),
        };
    }

    private async Task<NyxIdChatOperationResultSignal> ReconcileExactServiceCreateAsync(
        NyxIdChatOperationKey key,
        NyxIdChatOperationDispatchCommand command,
        AgentToolCredentialsPayload credentials,
        CancellationToken ct)
    {
        var tool = command.Tool;
        var admission = AgentToolOperationAdmissionPayloadMapper.FromPayload(
            tool?.OperationAdmission);
        JsonNode? arguments;
        try
        {
            arguments = JsonNode.Parse(tool?.ArgumentsJson ?? string.Empty);
        }
        catch (JsonException)
        {
            arguments = null;
        }

        if (tool is not { MayChangeExternalState: true } ||
            admission?.Identity is not AgentToolOperationIdentity.PublishedEndpoint ||
            arguments is not JsonObject)
        {
            return ExactFailure(
                key,
                "The exact-service recovery command was invalid.",
                NyxIdChatEffectEvidence.NotStarted);
        }

        NyxIdExactServiceApprovalCreateResult created;
        try
        {
            created = await _exactServiceApprovalPort.CreateAsync(
                    credentials.NyxIdAccessToken,
                    admission,
                    arguments,
                    key.OperationId,
                    key.OperationGeneration,
                    tool.IdempotencyKey,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Exact-service approval create recovery remained uncertain: operation={OperationId}",
                key.OperationId);
            return Uncertain(key);
        }

        if (created.Disposition is
            NyxIdExactServiceApprovalCreateDisposition.TierAUnavailable or
            NyxIdExactServiceApprovalCreateDisposition.ApprovalNotRequired)
        {
            return Uncertain(key);
        }

        if (created.Disposition != NyxIdExactServiceApprovalCreateDisposition.Created ||
            created.Snapshot is null)
        {
            return ExactFailure(
                key,
                "The exact-service approval authority could not be recovered.",
                NyxIdChatEffectEvidence.NotStarted);
        }

        return NyxIdChatTurnOperationExecutor.BuildExactServiceApprovalExecution(
                key,
                tool.CallId,
                tool.ToolName,
                created.Snapshot,
                tool.OperationAdmission?.ReadBack,
                new NyxIdChatTransientExecutionSession())
            .Result;
    }

    private async Task<NyxIdChatOperationResultSignal> ReconcileExactServiceDecisionAsync(
        NyxIdChatOperationKey key,
        NyxIdChatOperationDispatchCommand command,
        AgentToolCredentialsPayload credentials,
        CancellationToken ct)
    {
        var continuation = command.ToolApprovalContinuation;
        var authority = continuation?.ExactServiceApproval;
        if (continuation is null || authority is null ||
            !string.Equals(
                continuation.ApprovalRequestId,
                authority.RequestId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(continuation.ToolCallId) ||
            string.IsNullOrWhiteSpace(continuation.ToolName))
        {
            return ExactFailure(
                key,
                "The exact-service recovery authority was invalid.",
                NyxIdChatEffectEvidence.NotStarted);
        }

        NyxIdExactServiceApprovalSnapshot snapshot;
        try
        {
            snapshot = await _exactServiceApprovalPort.ObserveAsync(
                    credentials.NyxIdAccessToken,
                    authority,
                    ct)
                .ConfigureAwait(false);
            if (snapshot.State == NyxIdExactServiceApprovalState.Pending ||
                (!continuation.Approved &&
                 snapshot.State == NyxIdExactServiceApprovalState.Approved))
            {
                snapshot = await _exactServiceApprovalPort.DecideAsync(
                        credentials.NyxIdAccessToken,
                        authority,
                        continuation.Approved,
                        ct)
                    .ConfigureAwait(false);
            }

            if (continuation.Approved &&
                snapshot.State == NyxIdExactServiceApprovalState.Approved)
            {
                snapshot = await _exactServiceApprovalPort.RedeemAsync(
                        credentials.NyxIdAccessToken,
                        authority,
                        ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Exact-service approval decision recovery remained uncertain: request={RequestId}",
                authority.RequestId);
            return Uncertain(key);
        }

        return NyxIdChatTurnOperationExecutor.BuildExactServiceApprovalExecution(
                key,
                continuation.ToolCallId,
                continuation.ToolName,
                snapshot,
                continuation.OperationAdmission?.ReadBack,
                new NyxIdChatTransientExecutionSession())
            .Result;
    }

    private static NyxIdChatOperationResultSignal ExactFailure(
        NyxIdChatOperationKey key,
        string safeMessage,
        NyxIdChatEffectEvidence effect) => new()
    {
        Key = key.Clone(),
        Failure = new NyxIdChatOperationFailure
        {
            FailureCode = NyxIdChatTurnOperationExecutor.ExactServiceApprovalFailedCode,
            SafeMessage = safeMessage,
            ExternalEffect = effect,
        },
    };

    private async Task RevokeRecoveryCredentialAsync(
        NyxIdChatTurnOperationReconciliationInput input,
        CancellationToken ct)
    {
        try
        {
            await _secretVault!.RevokeAsync(new RevokeSecretRequest(
                input.RecoveryCredential.Ref,
                CredentialSecretPurposes.NyxIdChatRecoveryCredential,
                input.RecoveryCredential.OwnerScopeKey,
                input.RecoveryCredential.SubjectId,
                "nyxid chat recovery reached terminal proof"), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "NyxIdChat terminal recovery credential revocation failed: operation={OperationId}",
                input.Key.OperationId);
        }
    }

    private static bool IsOutcomeUncertain(NyxIdChatOperationResultSignal result) =>
        result.ResultCase == NyxIdChatOperationResultSignal.ResultOneofCase.Failure &&
        string.Equals(
            result.Failure.FailureCode,
            UnavailableNyxIdChatTurnOperationReconciliationPort.OutcomeUncertainCode,
            StringComparison.Ordinal);

    private bool IsValidCredentialReference(NyxIdChatTurnOperationReconciliationInput input) =>
        _secretVault is not null &&
        input.RecoveryContext is not null &&
        input.RecoveryCredential is
        {
            Ref.Length: > 0,
            OwnerScopeKey.Length: > 0,
            SubjectId.Length: > 0,
            SourceKind: DurableCallerCredentialSourceKind.NyxIdChat,
        } credential &&
        string.Equals(
            credential.Purpose,
            CredentialSecretPurposes.NyxIdChatRecoveryCredential,
            StringComparison.Ordinal) &&
        string.Equals(
            credential.OwnerScopeKey,
            $"nyxid-chat:{input.Key?.ConversationActorId}",
            StringComparison.Ordinal) &&
        string.Equals(
            credential.SubjectId,
            input.RecoveryContext.Caller?.OwnerSubject,
            StringComparison.Ordinal);

    private static NyxIdChatOperationResultSignal Uncertain(NyxIdChatOperationKey? key) => new()
    {
        Key = key?.Clone(),
        Failure = new NyxIdChatOperationFailure
        {
            FailureCode = UnavailableNyxIdChatTurnOperationReconciliationPort.OutcomeUncertainCode,
            SafeMessage = "The external operation may have changed state and could not be reconciled.",
            ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
        },
    };
}

public sealed class NyxIdChatTurnOperationDispatchPort
    : INyxIdChatTurnOperationDispatchPort
{
    private const string NyxIdApprovalFailedCode = "NYXID_APPROVAL_FAILED";
    private const string ExecutionFailedCode = "NYXID_CHAT_OPERATION_EXECUTION_FAILED";
    private const string ExecutionFailedMessage = "The operation could not be completed.";
    internal const string ExecutionCancelledCode = "NYXID_CHAT_OPERATION_CANCELLED";
    private const string ExecutionCancelledMessage = "The operation was cancelled.";

    private readonly INyxIdChatTurnOperationExecutor _executor;
    private readonly INyxIdChatTurnOperationReconciliationPort _reconciliationPort;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdChatTurnOperationDispatchPort> _logger;

    public NyxIdChatTurnOperationDispatchPort(
        INyxIdChatTurnOperationExecutor executor,
        INyxIdChatTurnOperationReconciliationPort reconciliationPort,
        IActorDispatchPort actorDispatchPort,
        TimeProvider timeProvider,
        ILogger<NyxIdChatTurnOperationDispatchPort> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _reconciliationPort = reconciliationPort ??
                              throw new ArgumentNullException(nameof(reconciliationPort));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public INyxIdChatTurnOperationDispatchSession OpenSession() => new Session(this);

    private Task DispatchExecutionAsync(
        string turnActorId,
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        string correlationId,
        ExecutionLease executionLease,
        Action<ExecutionLease> releaseLease,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnActorId);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();

        var frozenCommand = command.Clone();
        var canaryEffectFaultEligible = NyxIdChatCanaryEffectFaultDecisions.MatchesTurnDispatch(
            frozenCommand.Tool?.CanaryEffectFault,
            frozenCommand,
            session.Request?.ToolContext ?? session.StepState?.ToolContext,
            Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()));
        _ = Task.Run(
            () => ExecuteAndSignalAsync(
                turnActorId,
                frozenCommand,
                session,
                correlationId,
                canaryEffectFaultEligible,
                executionLease,
                releaseLease),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    private Task DispatchReconciliationAsync(
        string turnActorId,
        NyxIdChatTurnOperationReconciliationInput input,
        string correlationId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnActorId);
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        var frozenInput = input.Clone();
        _ = Task.Run(
            () => ReconcileAndSignalAsync(turnActorId, frozenInput, correlationId),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ExecuteAndSignalAsync(
        string turnActorId,
        NyxIdChatOperationDispatchCommand command,
        NyxIdChatTransientExecutionSession session,
        string correlationId,
        bool canaryEffectFaultEligible,
        ExecutionLease executionLease,
        Action<ExecutionLease> releaseLease)
    {
        NyxIdChatOperationResultSignal result;
        try
        {
            var execution = await _executor.ExecuteAsync(
                    command,
                    session,
                    (progress, token) => DispatchAsync(
                        turnActorId,
                        $"{command.Key.OperationId}:execution-progress:{progress.Sequence}",
                        new NyxIdChatTurnOperationExecutionProgressSignal
                        {
                            Progress = progress.Clone(),
                        },
                        correlationId,
                        token),
                    executionLease.Token)
                .ConfigureAwait(false);
            result = execution.Result?.Clone() ?? ExecutionFailure(command);
        }
        catch (OperationCanceledException) when (executionLease.IsCancellationRequested)
        {
            result = ExecutionCancelled(command);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "NyxIdChat background operation failed: turnActor={TurnActorId} operation={OperationId}",
                turnActorId,
                command.Key.OperationId);
            result = ExecutionFailure(command);
        }
        finally
        {
            releaseLease(executionLease);
        }

        if (canaryEffectFaultEligible &&
            IsCanaryEffectFaultBoundaryResult(command, result))
        {
            try
            {
                await DispatchCanaryEffectFaultAsync(
                        turnActorId,
                        command.Tool.CanaryEffectFault,
                        result,
                        correlationId)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "NyxIdChat canary result-boundary dispatch failed; falling back to the normal denied completion: turnActor={TurnActorId} operation={OperationId}",
                    turnActorId,
                    command.Key.OperationId);
                await DispatchCompletionAsync(
                        turnActorId,
                        result,
                        NyxIdChatTurnOperationCompletionSource.Execution,
                        correlationId)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            await DispatchCompletionAsync(
                    turnActorId,
                    result,
                    NyxIdChatTurnOperationCompletionSource.Execution,
                    correlationId)
                .ConfigureAwait(false);
        }
    }

    private async Task ReconcileAndSignalAsync(
        string turnActorId,
        NyxIdChatTurnOperationReconciliationInput input,
        string correlationId)
    {
        NyxIdChatOperationResultSignal result;
        try
        {
            result = (await _reconciliationPort
                    .ReconcileAsync(input, CancellationToken.None)
                    .ConfigureAwait(false))?.Clone() ?? ReconciliationFailure(input);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "NyxIdChat operation reconciliation failed: turnActor={TurnActorId} operation={OperationId}",
                turnActorId,
                input.Key?.OperationId);
            result = ReconciliationFailure(input);
        }

        await DispatchCompletionAsync(
                turnActorId,
                result,
                NyxIdChatTurnOperationCompletionSource.Reconciliation,
                correlationId)
            .ConfigureAwait(false);
    }

    private Task DispatchCompletionAsync(
        string turnActorId,
        NyxIdChatOperationResultSignal result,
        NyxIdChatTurnOperationCompletionSource source,
        string correlationId) =>
        DispatchAsync(
            turnActorId,
            $"{result.Key?.OperationId}:operation-completion:{source}",
            new NyxIdChatTurnOperationExecutionCompletedSignal
            {
                Result = result.Clone(),
                Source = source,
            },
            correlationId,
            CancellationToken.None);

    private Task DispatchCanaryEffectFaultAsync(
        string turnActorId,
        NyxIdChatCanaryEffectFaultDirective directive,
        NyxIdChatOperationResultSignal deniedResult,
        string correlationId) =>
        DispatchCoreAsync(
            turnActorId,
            $"{deniedResult.Key.OperationId}:canary-effect-fault:{directive.ArmId}",
            new NyxIdChatCanaryEffectFaultTriggeredSignal
            {
                ArmId = directive.ArmId,
                DeniedResult = deniedResult.Clone(),
                TriggeredAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            },
            correlationId,
            CancellationToken.None);

    internal static bool IsCanaryEffectFaultBoundaryResult(
        NyxIdChatOperationDispatchCommand? command,
        NyxIdChatOperationResultSignal? result)
    {
        var input = command?.Tool;
        var admission = input?.OperationAdmission;
        var tool = result?.Tool;
        var receipt = tool?.Receipt;
        return command?.Key is not null &&
               result?.Key is not null &&
               command.Key.Equals(result.Key) &&
               input?.CanaryEffectFault?.Key?.Equals(command.Key) == true &&
               !string.IsNullOrWhiteSpace(input.CallId) &&
               !string.IsNullOrWhiteSpace(input.ToolName) &&
               !string.IsNullOrWhiteSpace(admission?.ServiceInstanceId) &&
               string.Equals(
                   input.CanaryEffectFault.ServiceInstanceId,
                   admission.ServiceInstanceId,
                   StringComparison.Ordinal) &&
               tool?.ExternalEffect == NyxIdChatEffectEvidence.NotApplied &&
               receipt is
               {
                   Status: AgentToolReceiptStatus.Denied,
                   Effect: AgentToolReceiptEffect.Mutating,
                   NyxIdApprovalTerminalOutcome: NyxIdApprovalTerminalOutcome.Rejected,
               } &&
               receipt.NyxIdApprovalDecisionMode is
                   NyxIdApprovalDecisionMode.Unspecified or
                   NyxIdApprovalDecisionMode.PerRequest &&
               string.Equals(
                   receipt.ErrorCode,
                   NyxIdApprovalFailedCode,
                   StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(receipt.ApprovalRequestId) &&
               string.Equals(receipt.CallId, input.CallId, StringComparison.Ordinal) &&
               string.Equals(receipt.ToolName, input.ToolName, StringComparison.Ordinal) &&
               string.Equals(receipt.SubjectKind, "nyxid.user-service", StringComparison.Ordinal) &&
               string.Equals(
                   receipt.SubjectId,
                   admission.ServiceInstanceId,
                   StringComparison.Ordinal);
    }

    private async Task DispatchAsync(
        string actorId,
        string envelopeId,
        IMessage payload,
        string correlationId,
        CancellationToken ct)
    {
        try
        {
            await DispatchCoreAsync(actorId, envelopeId, payload, correlationId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "NyxIdChat background signal dispatch failed: actor={ActorId} envelope={EnvelopeId}",
                actorId,
                envelopeId);
        }
    }

    private Task DispatchCoreAsync(
        string actorId,
        string envelopeId,
        IMessage payload,
        string correlationId,
        CancellationToken ct) =>
        _actorDispatchPort.DispatchAsync(
            actorId,
            new EventEnvelope
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
                    CorrelationId = string.IsNullOrWhiteSpace(correlationId)
                        ? payload.Descriptor.FullName
                        : correlationId,
                },
            },
            ct);

    private static NyxIdChatOperationResultSignal ExecutionFailure(
        NyxIdChatOperationDispatchCommand command) => new()
    {
        Key = command.Key?.Clone(),
        Failure = new NyxIdChatOperationFailure
        {
            FailureCode = ExecutionFailedCode,
            SafeMessage = ExecutionFailedMessage,
            ExternalEffect = MayDispatchExternalEffect(command)
                ? NyxIdChatEffectEvidence.MayHaveChanged
                : NyxIdChatEffectEvidence.NotApplied,
        },
    };

    private static NyxIdChatOperationResultSignal ExecutionCancelled(
        NyxIdChatOperationDispatchCommand command) => new()
    {
        Key = command.Key?.Clone(),
        Failure = new NyxIdChatOperationFailure
        {
            FailureCode = ExecutionCancelledCode,
            SafeMessage = ExecutionCancelledMessage,
            ExternalEffect = MayDispatchExternalEffect(command)
                ? NyxIdChatEffectEvidence.MayHaveChanged
                : NyxIdChatEffectEvidence.NotApplied,
        },
    };

    private static NyxIdChatOperationResultSignal ReconciliationFailure(
        NyxIdChatTurnOperationReconciliationInput input) => new()
    {
        Key = input.Key?.Clone(),
        Failure = new NyxIdChatOperationFailure
        {
            FailureCode = UnavailableNyxIdChatTurnOperationReconciliationPort.OutcomeUncertainCode,
            SafeMessage = "The external operation may have changed state and could not be reconciled.",
            ExternalEffect = NyxIdChatEffectEvidence.MayHaveChanged,
        },
    };

    internal static bool MayDispatchExternalEffect(NyxIdChatOperationDispatchCommand command) =>
        command.Tool?.MayChangeExternalState == true ||
        (command.ToolApprovalContinuation is
         {
             Approved: true,
             MayChangeExternalState: true,
         });

    private sealed class Session(NyxIdChatTurnOperationDispatchPort owner)
        : INyxIdChatTurnOperationDispatchSession
    {
        private NyxIdChatTransientExecutionSession _executionSession = new();
        private ExecutionLease? _activeLease;

        public AgentToolExecutionContextPayload? CaptureToolContext() =>
            _executionSession.Request?.ToolContext?.Clone() ??
            _executionSession.StepState?.ToolContext?.Clone();

        public Task DispatchExecutionAsync(
            string turnActorId,
            NyxIdChatOperationDispatchCommand command,
            string correlationId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _activeLease) is not null)
                _executionSession = new NyxIdChatTransientExecutionSession();
            var lease = new ExecutionLease(command.Key, _executionSession);
            Volatile.Write(ref _activeLease, lease);
            return owner.DispatchExecutionAsync(
                turnActorId,
                command,
                lease.Session,
                correlationId,
                lease,
                CompleteExecution,
                ct);
        }

        public Task DispatchReconciliationAsync(
            string turnActorId,
            NyxIdChatTurnOperationReconciliationInput input,
            string correlationId,
            CancellationToken ct) =>
            owner.DispatchReconciliationAsync(
                turnActorId,
                input,
                correlationId,
                ct);

        public Task CancelExecutionAsync(NyxIdChatOperationKey key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var lease = Volatile.Read(ref _activeLease);
            if (lease is not null && KeysEqual(lease.Key, key))
                lease.Cancel();
            return Task.CompletedTask;
        }

        private void CompleteExecution(ExecutionLease lease)
        {
            Interlocked.CompareExchange(ref _activeLease, null, lease);
            lease.Dispose();
        }

        private static bool KeysEqual(NyxIdChatOperationKey? left, NyxIdChatOperationKey? right) =>
            left is not null && right is not null && left.Equals(right);
    }


    private sealed class ExecutionLease : IDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation = new();
        private bool _disposed;

        public ExecutionLease(
            NyxIdChatOperationKey? key,
            NyxIdChatTransientExecutionSession session)
        {
            Key = key?.Clone();
            Session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public NyxIdChatOperationKey? Key { get; }
        public NyxIdChatTransientExecutionSession Session { get; }
        public CancellationToken Token => _cancellation.Token;
        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public void Cancel()
        {
            lock (_sync)
            {
                if (!_disposed)
                    _cancellation.Cancel();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _cancellation.Dispose();
            }
        }
    }
}
