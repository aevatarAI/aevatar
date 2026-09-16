using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public enum ChannelWorkflowResultDeliveryRepairResultStatus
{
    Repaired = 0,
    AlreadyEnabled = 1,
    Repairing = 2,
    RepairFailed = 3,
    NotFound = 4,
    UnsupportedPlatform = 5,
}

public sealed record ChannelWorkflowResultDeliveryRepairResult(
    ChannelWorkflowResultDeliveryRepairResultStatus Status,
    string RequestId,
    string RegistrationId,
    string NyxAgentApiKeyId,
    ChannelWorkflowResultDeliveryRepairPhase FailurePhase =
        ChannelWorkflowResultDeliveryRepairPhase.Unspecified,
    ChannelWorkflowResultDeliveryRepairFailureReason FailureReason =
        ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified);

public interface IChannelWorkflowResultDeliveryRepairService
{
    Task<ChannelWorkflowResultDeliveryRepairResult> RepairAsync(
        string registrationId,
        string callerScopeId,
        string requestedBySubjectId,
        string accessToken,
        CancellationToken ct = default);
}

internal sealed class ChannelWorkflowResultDeliveryRepairService
    : IChannelWorkflowResultDeliveryRepairService
{
    private const int VaultStoreAttempts = 3;
    private static readonly TimeSpan CriticalCompletionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureObservationTimeout = TimeSpan.FromSeconds(10);

    private readonly IChannelBotRegistrationQueryPort _registrationQueryPort;
    private readonly IChannelWorkflowResultDeliveryRepairCommandPort _commandPort;
    private readonly IChannelWorkflowResultDeliveryRepairObservationPort _observationPort;
    private readonly IChannelWorkflowResultDeliveryRepairNyxPort _nyxPort;
    private readonly ISecretVault _secretVault;
    private readonly ILogger<ChannelWorkflowResultDeliveryRepairService> _logger;
    private readonly TimeProvider _timeProvider;

    public ChannelWorkflowResultDeliveryRepairService(
        IChannelBotRegistrationQueryPort registrationQueryPort,
        IChannelWorkflowResultDeliveryRepairCommandPort commandPort,
        IChannelWorkflowResultDeliveryRepairObservationPort observationPort,
        IChannelWorkflowResultDeliveryRepairNyxPort nyxPort,
        ISecretVault secretVault,
        ILogger<ChannelWorkflowResultDeliveryRepairService> logger,
        TimeProvider? timeProvider = null)
    {
        _registrationQueryPort = registrationQueryPort ??
            throw new ArgumentNullException(nameof(registrationQueryPort));
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _observationPort = observationPort ?? throw new ArgumentNullException(nameof(observationPort));
        _nyxPort = nyxPort ?? throw new ArgumentNullException(nameof(nyxPort));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ChannelWorkflowResultDeliveryRepairResult> RepairAsync(
        string registrationId,
        string callerScopeId,
        string requestedBySubjectId,
        string accessToken,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalizedRegistrationId = Normalize(registrationId);
        var normalizedCallerScopeId = Normalize(callerScopeId);
        var registration = await _registrationQueryPort.GetAsync(normalizedRegistrationId, ct);
        if (registration is null ||
            registration.Tombstoned ||
            string.IsNullOrWhiteSpace(normalizedCallerScopeId) ||
            !string.Equals(registration.ScopeId, normalizedCallerScopeId, StringComparison.Ordinal))
        {
            return Result(
                ChannelWorkflowResultDeliveryRepairResultStatus.NotFound,
                string.Empty,
                normalizedRegistrationId,
                string.Empty);
        }

        if (!string.Equals(registration.Platform, "lark", StringComparison.OrdinalIgnoreCase))
        {
            return Result(
                ChannelWorkflowResultDeliveryRepairResultStatus.UnsupportedPlatform,
                registration.WorkflowResultDeliveryRepair?.RequestId ?? string.Empty,
                registration.Id,
                registration.NyxAgentApiKeyId);
        }

        if (registration.WorkflowResultDeliveryRepair is null &&
            ChannelWorkflowResultDeliveryCapability.IsEnabled(registration))
        {
            return Result(
                ChannelWorkflowResultDeliveryRepairResultStatus.AlreadyEnabled,
                string.Empty,
                registration.Id,
                registration.NyxAgentApiKeyId);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBySubjectId);

        var repair = registration.WorkflowResultDeliveryRepair?.Clone();
        var newlyRequested = false;
        if (repair is null)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var requestedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            ChannelBotWorkflowResultDeliveryRepairOutcome outcome;
            try
            {
                outcome = await DispatchAndObserveAsync(
                    requestId,
                    ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Requested,
                    token => _commandPort.RequestAsync(
                        new ChannelBotWorkflowResultDeliveryRepairRequestCommand
                        {
                            RegistrationId = registration.Id,
                            RequestId = requestId,
                            ExpectedApiKeyId = registration.NyxAgentApiKeyId,
                            ExpectedConversationRouteId = registration.NyxConversationRouteId,
                            RequestedBySubjectId = requestedBySubjectId.Trim(),
                            RequestedAtUnixMs = requestedAtUnixMs,
                        },
                        token),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogFailure(registration.Id, requestId, "request_admission", ex);
                return Failed(
                    registration,
                    requestId,
                    ChannelWorkflowResultDeliveryRepairPhase.RequestAdmission,
                    ChannelWorkflowResultDeliveryRepairFailureReason.ObservationUnavailable);
            }

            if (outcome.OutcomeCase ==
                ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Rejected)
            {
                return FromRejected(registration, outcome.Rejected);
            }

            repair = outcome.Requested.Repair?.Clone();
            if (repair is null)
            {
                return Failed(
                    registration,
                    requestId,
                    ChannelWorkflowResultDeliveryRepairPhase.RequestAdmission,
                    ChannelWorkflowResultDeliveryRepairFailureReason.InvalidRequest);
            }

            newlyRequested = true;
        }

        if (string.IsNullOrWhiteSpace(repair.RequestId) ||
            string.IsNullOrWhiteSpace(repair.ExpectedApiKeyId) ||
            string.IsNullOrWhiteSpace(repair.ExpectedConversationRouteId))
        {
            return Failed(
                registration,
                repair.RequestId,
                ChannelWorkflowResultDeliveryRepairPhase.RequestAdmission,
                ChannelWorkflowResultDeliveryRepairFailureReason.InvalidRequest);
        }

        ct.ThrowIfCancellationRequested();
        if (HasPreparedCredential(registration, repair))
        {
            using var preparedCompletion = new CancellationTokenSource(
                CriticalCompletionTimeout,
                _timeProvider);
            return await RebindAndCompleteAsync(
                registration,
                repair,
                accessToken,
                preparedCompletion.Token);
        }

        var rotationSource = await ResolveRotationSourceKeyIdAsync(
            registration,
            repair,
            accessToken,
            newlyRequested,
            ct);
        if (rotationSource.Ambiguous)
        {
            return await RecordFailureAsync(
                registration,
                repair,
                ChannelWorkflowResultDeliveryRepairPhase.RotatedKeyRecovery,
                ChannelWorkflowResultDeliveryRepairFailureReason.AmbiguousRotatedKeyRecovery,
                repair.RotatedApiKeyId,
                repair.PreparedSecretReference);
        }

        ChannelRotatedNyxAgentCredential rotated;
        try
        {
            rotated = await _nyxPort.RotateAgentKeyAsync(
                accessToken,
                rotationSource.ApiKeyId,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(registration.Id, repair.RequestId, "api_key_rotation", ex);
            return await RecordFailureAsync(
                registration,
                repair,
                ChannelWorkflowResultDeliveryRepairPhase.ApiKeyRotation,
                ChannelWorkflowResultDeliveryRepairFailureReason.RotationFailed,
                repair.RotatedApiKeyId,
                repair.PreparedSecretReference);
        }

        using var criticalCompletion = new CancellationTokenSource(
            CriticalCompletionTimeout,
            _timeProvider);
        var criticalCt = criticalCompletion.Token;
        var storedReference = await PutWithBoundedRetriesAsync(
            registration,
            repair.RequestId,
            rotated,
            criticalCt);
        if (storedReference is null)
        {
            return await RecordFailureAsync(
                registration,
                repair,
                ChannelWorkflowResultDeliveryRepairPhase.VaultStorage,
                ChannelWorkflowResultDeliveryRepairFailureReason.VaultStorageFailed,
                rotated.ApiKeyId,
                null);
        }

        ChannelBotWorkflowResultDeliveryRepairOutcome preparedOutcome;
        try
        {
            preparedOutcome = await DispatchAndObserveAsync(
                repair.RequestId,
                ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Prepared,
                token => _commandPort.PrepareAsync(
                    new ChannelBotWorkflowResultDeliveryRepairPrepareCommand
                    {
                        RegistrationId = registration.Id,
                        RequestId = repair.RequestId,
                        ExpectedApiKeyId = repair.ExpectedApiKeyId,
                        RotatedApiKeyId = rotated.ApiKeyId,
                        PreparedSecretReference = storedReference.Clone(),
                        UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    },
                    token),
                criticalCt);
        }
        catch (Exception ex)
        {
            LogFailure(registration.Id, repair.RequestId, "credential_preparation", ex);
            return await RecordFailureAsync(
                registration,
                repair,
                ChannelWorkflowResultDeliveryRepairPhase.CredentialPreparation,
                ChannelWorkflowResultDeliveryRepairFailureReason.CompletionFailed,
                rotated.ApiKeyId,
                storedReference);
        }

        if (preparedOutcome.OutcomeCase ==
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Rejected)
        {
            return FromRejected(registration, preparedOutcome.Rejected);
        }

        var preparedRepair = preparedOutcome.Prepared.Repair?.Clone();
        if (preparedRepair is null || !HasPreparedCredential(registration, preparedRepair))
        {
            return await RecordFailureAsync(
                registration,
                repair,
                ChannelWorkflowResultDeliveryRepairPhase.CredentialPreparation,
                ChannelWorkflowResultDeliveryRepairFailureReason.InvalidRequest,
                rotated.ApiKeyId,
                storedReference);
        }

        return await RebindAndCompleteAsync(
            registration,
            preparedRepair,
            accessToken,
            criticalCt);
    }

    private async Task<ChannelWorkflowResultDeliveryRepairResult> RebindAndCompleteAsync(
        ChannelBotRegistrationEntry registration,
        ChannelWorkflowResultDeliveryRepairState repair,
        string accessToken,
        CancellationToken ct)
    {
        try
        {
            await _nyxPort.RebindConversationRouteAsync(
                accessToken,
                repair.ExpectedConversationRouteId,
                repair.RotatedApiKeyId,
                ct);
        }
        catch (Exception ex)
        {
            LogFailure(registration.Id, repair.RequestId, "route_rebinding", ex);
            return await RecordFailureAsync(
                registration,
                repair,
                ChannelWorkflowResultDeliveryRepairPhase.RouteRebinding,
                ChannelWorkflowResultDeliveryRepairFailureReason.RouteUpdateFailed,
                repair.RotatedApiKeyId,
                repair.PreparedSecretReference);
        }

        ChannelBotWorkflowResultDeliveryRepairOutcome completedOutcome;
        try
        {
            completedOutcome = await DispatchAndObserveAsync(
                repair.RequestId,
                ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Completed,
                token => _commandPort.CompleteAsync(
                    new ChannelBotWorkflowResultDeliveryRepairCompleteCommand
                    {
                        RegistrationId = registration.Id,
                        RequestId = repair.RequestId,
                        ExpectedApiKeyId = repair.ExpectedApiKeyId,
                        RotatedApiKeyId = repair.RotatedApiKeyId,
                        PreparedSecretReference = repair.PreparedSecretReference.Clone(),
                        UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    },
                    token),
                ct);
        }
        catch (Exception ex)
        {
            LogFailure(registration.Id, repair.RequestId, "actor_completion", ex);
            return await RecordFailureAsync(
                registration,
                repair,
                ChannelWorkflowResultDeliveryRepairPhase.ActorCompletion,
                ChannelWorkflowResultDeliveryRepairFailureReason.CompletionFailed,
                repair.RotatedApiKeyId,
                repair.PreparedSecretReference);
        }

        if (completedOutcome.OutcomeCase ==
            ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Rejected)
        {
            return FromRejected(registration, completedOutcome.Rejected);
        }

        return Result(
            ChannelWorkflowResultDeliveryRepairResultStatus.Repaired,
            repair.RequestId,
            registration.Id,
            repair.RotatedApiKeyId);
    }

    private async Task<SecretReference?> PutWithBoundedRetriesAsync(
        ChannelBotRegistrationEntry registration,
        string requestId,
        ChannelRotatedNyxAgentCredential rotated,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= VaultStoreAttempts; attempt++)
        {
            try
            {
                var stored = await _secretVault.PutAsync(
                    new StoreSecretRequest(
                        CredentialSecretPurposes.ChannelNyxIdAgentKey,
                        registration.ScopeId,
                        rotated.ApiKeyId,
                        rotated.FullKey,
                        $"channel-workflow-result-delivery-repair:{registration.Id}:{requestId}"),
                    ct);
                if (IsPreparedReferenceUsable(registration, stored.Reference))
                    return stored.Reference.Clone();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                LogFailure(registration.Id, requestId, $"vault_storage_attempt_{attempt}", ex);
            }
        }

        return null;
    }

    private async Task<RotationSourceResolution> ResolveRotationSourceKeyIdAsync(
        ChannelBotRegistrationEntry registration,
        ChannelWorkflowResultDeliveryRepairState repair,
        string accessToken,
        bool newlyRequested,
        CancellationToken ct)
    {
        if (repair.Status == ChannelWorkflowResultDeliveryRepairStatus.Failed &&
            repair.FailureReason ==
                ChannelWorkflowResultDeliveryRepairFailureReason.VaultStorageFailed &&
            !string.IsNullOrWhiteSpace(repair.RotatedApiKeyId))
        {
            return new RotationSourceResolution(repair.RotatedApiKeyId, false);
        }

        if (newlyRequested)
            return new RotationSourceResolution(repair.ExpectedApiKeyId, false);

        IReadOnlyList<ChannelNyxAgentKeySummary> keys;
        try
        {
            keys = await _nyxPort.ListAgentKeysAsync(accessToken, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(registration.Id, repair.RequestId, "rotated_key_recovery", ex);
            return new RotationSourceResolution(string.Empty, true);
        }

        var requestedAtUtc = repair.RequestedAtUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(repair.RequestedAtUnixMs)
            : DateTimeOffset.MinValue;
        var expectedName = ChannelWorkflowResultDeliveryRepairNyxPort.RelayKeyName(registration.Id);
        var expectedKeyIsActive = keys.Any(key =>
            key.IsActive &&
            string.Equals(key.ApiKeyId, repair.ExpectedApiKeyId, StringComparison.Ordinal));
        var candidates = keys
            .Where(key => key.IsActive)
            .Where(key => string.Equals(key.Name, expectedName, StringComparison.Ordinal))
            .Where(key => key.CreatedAtUtc >= requestedAtUtc)
            .Where(key => !string.Equals(
                key.ApiKeyId,
                repair.ExpectedApiKeyId,
                StringComparison.Ordinal))
            .Select(static key => key.ApiKeyId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return candidates.Length switch
        {
            0 when expectedKeyIsActive =>
                new RotationSourceResolution(repair.ExpectedApiKeyId, false),
            1 => new RotationSourceResolution(candidates[0], false),
            _ => new RotationSourceResolution(string.Empty, true),
        };
    }

    private async Task<ChannelWorkflowResultDeliveryRepairResult> RecordFailureAsync(
        ChannelBotRegistrationEntry registration,
        ChannelWorkflowResultDeliveryRepairState repair,
        ChannelWorkflowResultDeliveryRepairPhase phase,
        ChannelWorkflowResultDeliveryRepairFailureReason reason,
        string rotatedApiKeyId,
        SecretReference? preparedReference)
    {
        using var failureObservation = new CancellationTokenSource(
            FailureObservationTimeout,
            _timeProvider);
        try
        {
            var outcome = await DispatchAndObserveAsync(
                repair.RequestId,
                ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Failed,
                token => _commandPort.FailAsync(
                    new ChannelBotWorkflowResultDeliveryRepairFailCommand
                    {
                        RegistrationId = registration.Id,
                        RequestId = repair.RequestId,
                        ExpectedApiKeyId = repair.ExpectedApiKeyId,
                        RotatedApiKeyId = Normalize(rotatedApiKeyId),
                        PreparedSecretReference = preparedReference?.Clone(),
                        FailurePhase = phase,
                        FailureReason = reason,
                        UpdatedAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    },
                    token),
                failureObservation.Token);
            if (outcome.OutcomeCase ==
                ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Rejected)
            {
                return FromRejected(registration, outcome.Rejected);
            }

            return Failed(registration, repair.RequestId, phase, reason, rotatedApiKeyId);
        }
        catch (Exception ex)
        {
            LogFailure(registration.Id, repair.RequestId, "failure_observation", ex);
            return Failed(
                registration,
                repair.RequestId,
                phase,
                ChannelWorkflowResultDeliveryRepairFailureReason.ObservationUnavailable,
                rotatedApiKeyId);
        }
    }

    private async Task<ChannelBotWorkflowResultDeliveryRepairOutcome> DispatchAndObserveAsync(
        string requestId,
        ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase expected,
        Func<CancellationToken, Task<ChannelRegistrationCommandAcceptedReceipt>> dispatch,
        CancellationToken ct)
    {
        await using var observation = await _observationPort.BindAsync(requestId, ct);
        _ = await dispatch(ct);
        return await observation.WaitAsync(expected, ct);
    }

    private static bool HasPreparedCredential(
        ChannelBotRegistrationEntry registration,
        ChannelWorkflowResultDeliveryRepairState repair) =>
        !string.IsNullOrWhiteSpace(repair.RotatedApiKeyId) &&
        IsPreparedReferenceUsable(registration, repair.PreparedSecretReference);

    private static bool IsPreparedReferenceUsable(
        ChannelBotRegistrationEntry registration,
        SecretReference? reference) =>
        reference is not null &&
        !string.IsNullOrWhiteSpace(reference.Ref) &&
        string.Equals(
            reference.Purpose,
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            StringComparison.Ordinal) &&
        string.Equals(reference.OwnerScopeKey, registration.ScopeId, StringComparison.Ordinal);

    private static ChannelWorkflowResultDeliveryRepairResult FromRejected(
        ChannelBotRegistrationEntry registration,
        ChannelBotWorkflowResultDeliveryRepairRejectedEvent rejected) =>
        Failed(
            registration,
            rejected.RequestId,
            rejected.Phase,
            rejected.Reason);

    private static ChannelWorkflowResultDeliveryRepairResult Failed(
        ChannelBotRegistrationEntry registration,
        string requestId,
        ChannelWorkflowResultDeliveryRepairPhase phase,
        ChannelWorkflowResultDeliveryRepairFailureReason reason,
        string? rotatedApiKeyId = null) =>
        Result(
            ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed,
            requestId,
            registration.Id,
            string.IsNullOrWhiteSpace(rotatedApiKeyId)
                ? registration.NyxAgentApiKeyId
                : rotatedApiKeyId,
            phase,
            reason);

    private static ChannelWorkflowResultDeliveryRepairResult Result(
        ChannelWorkflowResultDeliveryRepairResultStatus status,
        string requestId,
        string registrationId,
        string apiKeyId,
        ChannelWorkflowResultDeliveryRepairPhase failurePhase =
            ChannelWorkflowResultDeliveryRepairPhase.Unspecified,
        ChannelWorkflowResultDeliveryRepairFailureReason failureReason =
            ChannelWorkflowResultDeliveryRepairFailureReason.Unspecified) =>
        new(
            status,
            Normalize(requestId),
            Normalize(registrationId),
            Normalize(apiKeyId),
            failurePhase,
            failureReason);

    private void LogFailure(
        string registrationId,
        string requestId,
        string phase,
        Exception exception) =>
        _logger.LogWarning(
            "Channel workflow result delivery repair phase failed: registrationId={RegistrationId}, requestId={RequestId}, phase={Phase}, exceptionType={ExceptionType}",
            registrationId,
            requestId,
            phase,
            exception.GetType().Name);

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private sealed record RotationSourceResolution(string ApiKeyId, bool Ambiguous);
}
