using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

public interface INyxIdChatTurnOperationDispatchPort
{
    INyxIdChatTurnOperationDispatchSession OpenSession();
}

public interface INyxIdChatTurnOperationDispatchSession
{
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
/// Production Tier-B recovery path. It executes only the read operation frozen
/// in the original effect admission; missing credentials or proof stay honest
/// uncertainty and never cause the effect to be dispatched again.
/// </summary>
public sealed class AdmittedNyxIdChatTurnOperationReconciliationPort
    : INyxIdChatTurnOperationReconciliationPort
{
    private readonly INyxIdChatToolVerificationPort _verificationPort;
    private readonly ISecretVault? _secretVault;
    private readonly INyxIdChatDelegationCredentialLifecyclePort _delegationCredentialLifecycle;
    private readonly ILogger<AdmittedNyxIdChatTurnOperationReconciliationPort> _logger;

    public AdmittedNyxIdChatTurnOperationReconciliationPort(
        INyxIdChatToolVerificationPort verificationPort)
        : this(
            verificationPort,
            null,
            new NyxIdChatDelegationCredentialLifecyclePort(TimeProvider.System),
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
            NullLogger<AdmittedNyxIdChatTurnOperationReconciliationPort>.Instance)
    {
    }

    public AdmittedNyxIdChatTurnOperationReconciliationPort(
        INyxIdChatToolVerificationPort verificationPort,
        ISecretVault? secretVault,
        INyxIdChatDelegationCredentialLifecyclePort delegationCredentialLifecycle,
        ILogger<AdmittedNyxIdChatTurnOperationReconciliationPort> logger)
    {
        _verificationPort = verificationPort;
        _secretVault = secretVault;
        _delegationCredentialLifecycle = delegationCredentialLifecycle;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NyxIdChatOperationResultSignal> ReconcileAsync(
        NyxIdChatTurnOperationReconciliationInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Key is null ||
            !NyxIdChatOperationAdmissionPolicy.IsValidReadBack(input.ReadBack) ||
            !IsValidCredentialReference(input))
            return Uncertain(input.Key);

        var resolved = await _secretVault!.ResolveAsync(new ResolveSecretRequest(
            input.RecoveryCredential.Ref,
            CredentialSecretPurposes.NyxIdChatRecoveryCredential,
            input.RecoveryCredential.OwnerScopeKey,
            input.RecoveryCredential.SubjectId,
            "nyxid chat effect read-back recovery"), ct).ConfigureAwait(false);
        if (!resolved.Resolved || !TryParseCredentials(resolved.Secret, out var credentials))
            return Uncertain(input.Key);

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
                        Convert.ToBase64String(credentials.ToByteArray()),
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
            try
            {
                await _secretVault.RevokeAsync(new RevokeSecretRequest(
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
        return new NyxIdChatOperationResultSignal
        {
            Key = input.Key.Clone(),
            ToolVerification = verification,
        };
    }

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

    private static bool TryParseCredentials(
        string? encoded,
        out AgentToolCredentialsPayload credentials)
    {
        credentials = null!;
        try
        {
            credentials = AgentToolCredentialsPayload.Parser.ParseFrom(
                Convert.FromBase64String(encoded ?? string.Empty));
            return credentials is not null;
        }
        catch (Exception exception) when (exception is FormatException or InvalidProtocolBufferException)
        {
            return false;
        }
    }

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
        _ = Task.Run(
            () => ExecuteAndSignalAsync(
                turnActorId,
                frozenCommand,
                session,
                correlationId,
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

        try
        {
            await DispatchCompletionAsync(
                    turnActorId,
                    result,
                    NyxIdChatTurnOperationCompletionSource.Execution,
                    correlationId)
                .ConfigureAwait(false);
        }
        finally
        {
            releaseLease(executionLease);
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

    private async Task DispatchAsync(
        string actorId,
        string envelopeId,
        IMessage payload,
        string correlationId,
        CancellationToken ct)
    {
        try
        {
            await _actorDispatchPort.DispatchAsync(
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
                    ct)
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
         }) ||
        command.PlanGateContinuation?.MayChangeExternalState == true;

    private sealed class Session(NyxIdChatTurnOperationDispatchPort owner)
        : INyxIdChatTurnOperationDispatchSession
    {
        private NyxIdChatTransientExecutionSession _executionSession = new();
        private ExecutionLease? _activeLease;

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
