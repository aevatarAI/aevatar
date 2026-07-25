using System.Security.Cryptography;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Application.CodexExecution;

public enum ManagedCodexCredentialReadinessMode
{
    Normal = 0,
    ForceRemoteValidation = 1,
}

public interface IManagedCodexCredentialLifecycle
{
    Task<ManagedCodexCredentialDescriptor> EnsureReadyAsync(
        ExternalSubjectRef owner,
        string? bearerToken,
        ManagedCodexCredentialReadinessMode mode,
        CancellationToken ct = default);

    Task<ManagedCodexCredentialMutationResult> ProvisionAsync(
        string bearerToken,
        string authenticatedUserId,
        CancellationToken ct = default);

    Task<ManagedCodexCredentialMutationResult> RotateAsync(
        string bearerToken,
        string authenticatedUserId,
        CancellationToken ct = default);

    Task<ManagedCodexCredentialMutationResult> RevokeAsync(
        string bearerToken,
        string authenticatedUserId,
        CancellationToken ct = default);
}

public sealed record ManagedCodexCredentialMutationResult(
    string Status,
    string ActorId,
    string ApiKeyId,
    long ExpiresAtUnixMs,
    string CommandId);

public sealed class ManagedCodexCredentialLifecycleException : Exception
{
    public ManagedCodexCredentialLifecycleException(
        string code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class ManagedCodexCredentialLifecycle(
    IOptions<ManagedCodexOptions> options,
    IManagedCodexNyxIdCredentialPort nyxIdPort,
    ISecretVault secretVault,
    IManagedCodexCredentialQueryPort queryPort,
    IManagedCodexCredentialCommandPort commandPort,
    IManagedCodexCredentialMutationLease mutationLease,
    IManagedCodexCredentialReadinessObservationPort readinessObservation,
    TimeProvider timeProvider,
    ILogger<ManagedCodexCredentialLifecycle> logger) : IManagedCodexCredentialLifecycle
{
    internal const string CredentialName = "aevatar-managed-codex";
    private static readonly TimeSpan ReadyCleanupHandoffReserve =
        TimeSpan.FromSeconds(10);

    private readonly ManagedCodexOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly IManagedCodexNyxIdCredentialPort _nyxIdPort =
        nyxIdPort ?? throw new ArgumentNullException(nameof(nyxIdPort));
    private readonly ISecretVault _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
    private readonly IManagedCodexCredentialQueryPort _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    private readonly IManagedCodexCredentialCommandPort _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
    private readonly IManagedCodexCredentialMutationLease _mutationLease =
        mutationLease ?? throw new ArgumentNullException(nameof(mutationLease));
    private readonly IManagedCodexCredentialReadinessObservationPort _readinessObservation =
        readinessObservation ?? throw new ArgumentNullException(nameof(readinessObservation));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<ManagedCodexCredentialLifecycle> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ManagedCodexNyxIdCatalogResolver _catalogResolver = new();

    private enum ReadinessRepairKind
    {
        None = 0,
        ReconcileCurrent = 1,
        AdoptRemote = 2,
        Replace = 3,
    }

    private sealed record ReadinessRepairPlan(
        ReadinessRepairKind Kind,
        ManagedCodexCredentialDescriptor? Current,
        ManagedCodexNyxIdEligibility Eligibility,
        ManagedCodexNyxIdApiKey? Remote,
        SecretReference? RemoteReference,
        bool UpdateRemotePolicy,
        IReadOnlyList<string> ApiKeyIdsToRevoke);

    private sealed record ReadinessRepairOutcome(
        ManagedCodexCredentialDescriptor ExpectedCredential,
        ManagedCodexCredentialDescriptor? PreviousCredentialToRetire);

    private abstract record ConcurrentReadinessOutcome;

    private sealed record ConcurrentCredentialCommitted(
        ManagedCodexCredentialDescriptor Credential) : ConcurrentReadinessOutcome;

    private sealed record ConcurrentMutationLeaseAcquired(
        IManagedCodexCredentialMutationLeaseHandle Lease,
        ManagedCodexCredentialSnapshot Trigger,
        OutcomeDeadline OutcomeDeadline) : ConcurrentReadinessOutcome;

    private sealed class OutcomeDeadline(
        TimeProvider timeProvider,
        DateTimeOffset primaryExpiresAt,
        DateTimeOffset compensationExpiresAt,
        DateTimeOffset recordingExpiresAt)
    {
        public CancellationTokenSource BeginPrimary(TimeSpan reserve = default) =>
            Begin(primaryExpiresAt - reserve);

        public CancellationTokenSource BeginCompensation() =>
            Begin(compensationExpiresAt);

        public CancellationTokenSource BeginRecording() =>
            Begin(recordingExpiresAt);

        private CancellationTokenSource Begin(DateTimeOffset expiresAt)
        {
            var remaining = expiresAt - timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
                return new CancellationTokenSource(remaining, timeProvider);

            var expired = new CancellationTokenSource();
            expired.Cancel();
            return expired;
        }
    }

    private sealed class ManualMutationScope(
        OutcomeDeadline outcomeDeadline,
        IManagedCodexCredentialMutationLeaseHandle lease,
        CancellationTokenSource primary,
        CancellationTokenSource preMutationWait) : IAsyncDisposable
    {
        public OutcomeDeadline OutcomeDeadline { get; } = outcomeDeadline;
        public CancellationToken PrimaryToken => primary.Token;
        public CancellationToken PreMutationToken => preMutationWait.Token;

        public async ValueTask DisposeAsync()
        {
            preMutationWait.Dispose();
            primary.Dispose();
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<ManagedCodexCredentialDescriptor> EnsureReadyAsync(
        ExternalSubjectRef owner,
        string? bearerToken,
        ManagedCodexCredentialReadinessMode mode,
        CancellationToken ct = default)
    {
        EnsureEnabled();
        ValidateReadinessRequest(owner, mode);

        var projected = await _queryPort.ResolveAsync(owner, ct).ConfigureAwait(false);
        if (CanReturnNormalReady(projected, owner, bearerToken, mode))
            return projected!.Credential.Clone();

        await using var observation = await _readinessObservation.BindAsync(owner, ct)
            .ConfigureAwait(false);
        projected = await _queryPort.ResolveAsync(owner, ct).ConfigureAwait(false);
        if (CanReturnNormalReady(projected, owner, bearerToken, mode))
            return projected!.Credential.Clone();

        var ownerKey = ManagedCodexCredentialActorIdentity.From(owner);
        var outcomeDeadline = BeginOutcomeDeadline(_timeProvider.GetUtcNow());
        IManagedCodexCredentialMutationLeaseHandle? lease;
        using (var acquisitionDeadline = outcomeDeadline.BeginPrimary())
        using (var acquisitionWait = CancellationTokenSource.CreateLinkedTokenSource(
                   ct,
                   acquisitionDeadline.Token))
        {
            try
            {
                lease = await _mutationLease.TryAcquireAsync(
                        ownerKey,
                        acquisitionWait.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw CommitTimeout();
            }
        }

        ManagedCodexCredentialSnapshot? reacquisitionTrigger = null;
        if (lease is null)
        {
            using var concurrentDeadline = outcomeDeadline.BeginPrimary();
            using var concurrentWait =
                CancellationTokenSource.CreateLinkedTokenSource(
                    ct,
                    concurrentDeadline.Token);
            try
            {
                var outcome = await WaitForConcurrentReadinessAsync(
                        observation,
                        owner,
                        projected,
                        bearerToken,
                        mode,
                        concurrentWait.Token,
                        ct)
                    .ConfigureAwait(false);
                if (outcome is ConcurrentCredentialCommitted committed)
                    return committed.Credential;

                var acquired = (ConcurrentMutationLeaseAcquired)outcome;
                lease = acquired.Lease;
                reacquisitionTrigger = acquired.Trigger;
                outcomeDeadline = acquired.OutcomeDeadline;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw CommitTimeout();
            }
        }

        using var preMutationDeadline = outcomeDeadline.BeginPrimary();
        using var preMutationWait = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            preMutationDeadline.Token);
        return await EnsureReadyAsLeaseOwnerAsync(
                observation,
                owner,
                bearerToken,
                mode,
                lease,
                reacquisitionTrigger,
                outcomeDeadline,
                preMutationWait.Token,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<ManagedCodexCredentialDescriptor> EnsureReadyAsLeaseOwnerAsync(
        IManagedCodexCredentialReadinessObservationLease observation,
        ExternalSubjectRef owner,
        string? bearerToken,
        ManagedCodexCredentialReadinessMode mode,
        IManagedCodexCredentialMutationLeaseHandle lease,
        ManagedCodexCredentialSnapshot? reacquisitionTrigger,
        OutcomeDeadline outcomeDeadline,
        CancellationToken preMutationCt,
        CancellationToken requestCt)
    {
        IManagedCodexCredentialMutationLeaseHandle? ownedLease = lease;
        CancellationTokenSource? completion = null;
        try
        {
            var queried = await _queryPort.ResolveAsync(owner, preMutationCt)
                .ConfigureAwait(false);
            var projected = SelectNewestCommittedSnapshot(
                queried,
                reacquisitionTrigger);
            if (CanReturnNormalReady(projected, owner, bearerToken, mode))
                return projected!.Credential.Clone();

            var normalizedBearer = RequireBearerToken(bearerToken);
            await VerifyBearerOwnerAsync(
                    normalizedBearer,
                    owner.ExternalUserId,
                    preMutationCt)
                .ConfigureAwait(false);
            var projectedReady = IsReady(
                projected?.Credential,
                owner,
                _timeProvider.GetUtcNow());
            if (mode == ManagedCodexCredentialReadinessMode.Normal && projectedReady)
            {
                requestCt.ThrowIfCancellationRequested();
                var readySnapshot = projected!;
                using var cleanupAttempt = outcomeDeadline.BeginPrimary(
                    ReadyCleanupHandoffReserve);
                using var cleanupWait =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        requestCt,
                        cleanupAttempt.Token);
                bool cleanupCompleted;
                try
                {
                    cleanupCompleted = await TryRetryPendingCleanupAsync(
                        normalizedBearer,
                        owner,
                        readySnapshot.PendingRevocations,
                        cleanupWait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!requestCt.IsCancellationRequested &&
                          cleanupAttempt.IsCancellationRequested)
                {
                    cleanupCompleted = false;
                }
                if (!cleanupCompleted)
                    LogPendingCleanup(readySnapshot.PendingRevocations);

                await ownedLease.DisposeAsync().ConfigureAwait(false);
                ownedLease = null;
                using var handoffCompletion = outcomeDeadline.BeginPrimary();
                _ = await ConfirmReadinessForReconciliationAsync(
                        owner,
                        readySnapshot.Credential,
                        ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed,
                        handoffCompletion.Token)
                    .ConfigureAwait(false);
                try
                {
                    return await WaitForReadyAsync(
                            observation,
                            owner,
                            ManagedCodexCredentialReadinessMode.Normal,
                            handoffCompletion.Token,
                            readySnapshot.StateVersion,
                            readySnapshot.Credential)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw CommitTimeout();
                }
            }

            CancellationToken BeginCompletion()
            {
                requestCt.ThrowIfCancellationRequested();
                completion ??= outcomeDeadline.BeginPrimary();
                return completion.Token;
            }

            var repairCt = preMutationCt;
            if (!projectedReady && projected?.PendingRevocations.Count > 0)
            {
                repairCt = BeginCompletion();
                if (!await TryRetryPendingCleanupAsync(
                        normalizedBearer,
                        owner,
                        projected.PendingRevocations,
                        repairCt).ConfigureAwait(false))
                {
                    throw CleanupPending();
                }
            }

            var repair = await SelectReadinessRepairAsync(
                normalizedBearer,
                owner,
                projected?.Credential,
                repairCt).ConfigureAwait(false);
            var completionCt = BeginCompletion();
            var repairOutcome = await ExecuteReadinessRepairAsync(
                normalizedBearer,
                owner,
                repair,
                outcomeDeadline,
                completionCt).ConfigureAwait(false);

            ManagedCodexCredentialDescriptor committed;
            try
            {
                committed = await WaitForReadyAsync(
                        observation,
                        owner,
                        mode,
                        completionCt,
                        projected?.StateVersion ?? 0,
                        repairOutcome.ExpectedCredential)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw CommitTimeout();
            }

            var committedCleanup = new List<ManagedCodexCredentialCleanup>();
            if (projectedReady && projected?.PendingRevocations.Count > 0)
            {
                committedCleanup.AddRange(
                    projected.PendingRevocations.Select(static item => item.Clone()));
            }
            if (repairOutcome.PreviousCredentialToRetire is not null)
            {
                committedCleanup.Add(BuildPreviousCredentialCleanup(
                    repairOutcome.PreviousCredentialToRetire,
                    committed));
            }
            await RetryCleanupAfterReadinessAsync(
                    normalizedBearer,
                    owner,
                    committedCleanup,
                    outcomeDeadline,
                    requestCt)
                .ConfigureAwait(false);
            return committed;
        }
        finally
        {
            completion?.Dispose();
            if (ownedLease is not null)
                await ownedLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ConcurrentReadinessOutcome> WaitForConcurrentReadinessAsync(
        IManagedCodexCredentialReadinessObservationLease observation,
        ExternalSubjectRef owner,
        ManagedCodexCredentialSnapshot? baseline,
        string? bearerToken,
        ManagedCodexCredentialReadinessMode mode,
        CancellationToken waitCt,
        CancellationToken requestCt)
    {
        var minimumStateVersion = baseline?.StateVersion ?? 0;
        var ownerKey = ManagedCodexCredentialActorIdentity.From(owner);
        var reacquisitionAttempted = false;
        await foreach (var snapshot in observation.ReadAllAsync(waitCt).ConfigureAwait(false))
        {
            if (snapshot.StateVersion <= minimumStateVersion ||
                !IsReady(snapshot.Credential, owner, _timeProvider.GetUtcNow()))
            {
                continue;
            }

            if (HasSufficientReadinessEvidence(snapshot.ReadinessEvidence, mode))
            {
                return new ConcurrentCredentialCommitted(
                    snapshot.Credential!.Clone());
            }

            if (mode != ManagedCodexCredentialReadinessMode.ForceRemoteValidation ||
                snapshot.ReadinessEvidence !=
                ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed ||
                reacquisitionAttempted)
            {
                continue;
            }

            reacquisitionAttempted = true;
            var outcomeDeadline = BeginOutcomeDeadline(_timeProvider.GetUtcNow());
            using var acquisitionDeadline = outcomeDeadline.BeginPrimary();
            using var acquisitionWait =
                CancellationTokenSource.CreateLinkedTokenSource(
                    requestCt,
                    acquisitionDeadline.Token);
            var lease = await _mutationLease.TryAcquireAsync(
                    ownerKey,
                    acquisitionWait.Token)
                .ConfigureAwait(false);
            if (lease is null)
                continue;

            try
            {
                _ = RequireBearerToken(bearerToken);
                return new ConcurrentMutationLeaseAcquired(
                    lease,
                    snapshot.Clone(),
                    outcomeDeadline);
            }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        throw CommitTimeout();
    }

    private static ManagedCodexCredentialSnapshot? SelectNewestCommittedSnapshot(
        ManagedCodexCredentialSnapshot? queried,
        ManagedCodexCredentialSnapshot? trigger)
    {
        if (trigger is null)
            return queried;
        if (queried is null || queried.StateVersion < trigger.StateVersion)
            return trigger.Clone();
        return queried;
    }

    private static string RequireBearerToken(string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            throw Failure(
                "managed_user_authorization_unavailable",
                "Managed Codex credential creation or repair requires the current user's authorization.");
        }

        return bearerToken.Trim();
    }

    public async Task<ManagedCodexCredentialMutationResult> ProvisionAsync(
        string bearerToken,
        string authenticatedUserId,
        CancellationToken ct = default)
    {
        EnsureEnabled();
        var owner = await ResolveOwnerAsync(bearerToken, authenticatedUserId, requireEligibility: true, ct);
        await using var mutation = await BeginManualMutationAsync(owner, ct)
            .ConfigureAwait(false);
        var outcomeDeadline = mutation.OutcomeDeadline;
        var preMutationCt = mutation.PreMutationToken;
        var existing = await _queryPort.ResolveAsync(owner, preMutationCt)
            .ConfigureAwait(false);
        preMutationCt.ThrowIfCancellationRequested();
        if (!await TryRetryPendingCleanupAsync(
                bearerToken,
                owner,
                existing?.PendingRevocations,
                mutation.PrimaryToken).ConfigureAwait(false))
        {
            throw CleanupPending();
        }
        if (existing?.Credential.Status == ManagedCodexCredentialStatus.Active)
            throw Failure("managed_credential_already_provisioned", "Managed Codex is already provisioned for this user; rotate it instead.");

        var eligibility = await _catalogResolver.ResolveAsync(
                _nyxIdPort,
                bearerToken,
                preMutationCt)
            .ConfigureAwait(false);
        var activeKeys = await GetActiveManagedKeysAsync(
                bearerToken,
                preMutationCt)
            .ConfigureAwait(false);
        EnsureAtMostOneActiveKey(activeKeys);
        if (activeKeys.Count == 1)
        {
            var recovered = await TryReconcileProvisionAsync(
                bearerToken,
                owner,
                activeKeys[0],
                eligibility,
                outcomeDeadline,
                mutation.PrimaryToken).ConfigureAwait(false);
            if (recovered is not null)
                return recovered;
        }

        ct.ThrowIfCancellationRequested();
        mutation.PrimaryToken.ThrowIfCancellationRequested();
        var mutationCt = mutation.PrimaryToken;
        var requestedExpiresAt = _timeProvider.GetUtcNow().AddDays(_options.CredentialLifetimeDays);
        var issued = await _nyxIdPort.CreateApiKeyAsync(
            bearerToken,
            IssueRequest(eligibility, requestedExpiresAt),
            mutationCt).ConfigureAwait(false);
        ManagedCodexNyxIdApiKey persistedKey;
        try
        {
            ValidateIssuedKey(issued.Key, eligibility);
            persistedKey = await RequirePersistedIssuedKeyAsync(
                bearerToken,
                issued.Key.Id,
                eligibility,
                requestedExpiresAt,
                mutationCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(issued.Key.Id))
            {
                _ = await CompensateRejectedIssuanceWithinOutcomeAsync(
                    bearerToken,
                    owner,
                    issued.Key.Id,
                    outcomeDeadline).ConfigureAwait(false);
            }
            throw;
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            if (!string.IsNullOrWhiteSpace(issued.Key.Id))
            {
                _ = await CompensateRejectedIssuanceWithinOutcomeAsync(
                    bearerToken,
                    owner,
                    issued.Key.Id,
                    outcomeDeadline).ConfigureAwait(false);
            }
            throw;
        }

        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        var expiresAt = persistedKey.ExpiresAt!.Value;
        var requestedRef = SecretRefFor(actorId, persistedKey.Id);
        StoreSecretResult stored;
        try
        {
            stored = await issued.Secret.UseAsync(secret => _secretVault.PutAsync(
                new StoreSecretRequest(
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    actorId,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    secret,
                    "managed-codex-provision",
                    expiresAt,
                    requestedRef),
                mutationCt)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CompensateUnadoptedWithinOutcomeAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                outcomeDeadline).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await CompensateUnadoptedWithinOutcomeAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                outcomeDeadline).ConfigureAwait(false);
            throw Failure(
                "managed_credential_vault_store_failed",
                "The managed Codex credential could not be stored securely.");
        }

        SecretReference reference;
        try
        {
            reference = ValidateStoredReference(stored.Reference, requestedRef, actorId, expiresAt);
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            await CompensateUnadoptedWithinOutcomeAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                outcomeDeadline).ConfigureAwait(false);
            throw;
        }
        var descriptor = BuildDescriptor(
            owner,
            persistedKey.Id,
            reference,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId,
            expiresAt);
        return await CommitManualProvisionAsync(
                descriptor,
                actorId,
                persistedKey.Id,
                expiresAt,
                outcomeDeadline)
            .ConfigureAwait(false);
    }

    private async Task<ManagedCodexCredentialMutationResult> CommitManualProvisionAsync(
        ManagedCodexCredentialDescriptor descriptor,
        string actorId,
        string apiKeyId,
        DateTimeOffset expiresAt,
        OutcomeDeadline outcomeDeadline)
    {
        DispatchAdmission admission;
        try
        {
            using var recording = outcomeDeadline.BeginRecording();
            admission = await _commandPort.CommitProvisionedAsync(
                    descriptor,
                    recording.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            throw Failure(
                "managed_credential_persistence_pending",
                "Managed Codex credential persistence is pending reconciliation.");
        }
        if (!admission.Accepted)
        {
            throw Failure(
                "managed_credential_persistence_pending",
                "Managed Codex credential persistence is pending reconciliation.");
        }

        return Result("provisioning_accepted", actorId, apiKeyId, expiresAt, admission);
    }

    public async Task<ManagedCodexCredentialMutationResult> RotateAsync(
        string bearerToken,
        string authenticatedUserId,
        CancellationToken ct = default)
    {
        EnsureEnabled();
        var owner = await ResolveOwnerAsync(bearerToken, authenticatedUserId, requireEligibility: true, ct);
        await using var mutation = await BeginManualMutationAsync(owner, ct)
            .ConfigureAwait(false);
        var outcomeDeadline = mutation.OutcomeDeadline;
        var preMutationCt = mutation.PreMutationToken;
        var current = await RequireActiveAsync(owner, preMutationCt)
            .ConfigureAwait(false);
        preMutationCt.ThrowIfCancellationRequested();
        if (!await TryRetryPendingCleanupAsync(
                bearerToken,
                owner,
                current.PendingRevocations,
                mutation.PrimaryToken).ConfigureAwait(false))
        {
            throw CleanupPending();
        }
        var eligibility = await _catalogResolver.ResolveAsync(
                _nyxIdPort,
                bearerToken,
                preMutationCt)
            .ConfigureAwait(false);
        if (!string.Equals(
                current.Credential.ChronoSandboxUserServiceId,
                eligibility.ChronoSandboxUserServiceId,
                StringComparison.Ordinal))
        {
            throw Failure(
                "chrono_sandbox_service_changed",
                "The user's chrono-sandbox service changed; revoke and provision a new scoped credential.");
        }

        ValidateActiveDescriptor(current.Credential, owner, _timeProvider.GetUtcNow());
        var activeKeys = await GetActiveManagedKeysAsync(
                bearerToken,
                preMutationCt)
            .ConfigureAwait(false);
        EnsureAtMostOneActiveKey(activeKeys);
        if (activeKeys.Count == 1 &&
            !string.Equals(
                activeKeys[0].Id,
                current.Credential.ApiKeyId,
                StringComparison.Ordinal))
        {
            var recovered = await TryReconcileRotationAsync(
                bearerToken,
                owner,
                current.Credential,
                activeKeys[0],
                eligibility,
                outcomeDeadline,
                mutation.PrimaryToken).ConfigureAwait(false);
            if (recovered is not null)
                return recovered;
            activeKeys = [];
        }

        ct.ThrowIfCancellationRequested();
        mutation.PrimaryToken.ThrowIfCancellationRequested();
        var mutationCt = mutation.PrimaryToken;
        var expiresAt = current.Credential.ExpiresAt.ToDateTimeOffset();
        ManagedCodexNyxIdIssuedApiKey issued;
        if (activeKeys.Count == 1)
        {
            ValidatePersistedKey(
                activeKeys[0],
                eligibility,
                expiresAt);
            issued = await _nyxIdPort.RotateApiKeyAsync(
                bearerToken,
                current.Credential.ApiKeyId,
                mutationCt).ConfigureAwait(false);
        }
        else
        {
            issued = await _nyxIdPort.CreateApiKeyAsync(
                bearerToken,
                IssueRequest(eligibility, expiresAt),
                mutationCt).ConfigureAwait(false);
        }

        ManagedCodexNyxIdApiKey persistedKey;
        try
        {
            ValidateIssuedKey(issued.Key, eligibility);
            persistedKey = await RequirePersistedIssuedKeyAsync(
                bearerToken,
                issued.Key.Id,
                eligibility,
                expiresAt,
                mutationCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(issued.Key.Id))
            {
                _ = await CompensateRejectedIssuanceWithinOutcomeAsync(
                    bearerToken,
                    owner,
                    issued.Key.Id,
                    outcomeDeadline).ConfigureAwait(false);
            }
            throw;
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            _ = await CompensateRejectedIssuanceWithinOutcomeAsync(
                bearerToken,
                owner,
                issued.Key.Id,
                outcomeDeadline).ConfigureAwait(false);
            throw;
        }

        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        var requestedRef = SecretRefFor(actorId, persistedKey.Id);
        StoreSecretResult stored;
        try
        {
            stored = await issued.Secret.UseAsync(secret => _secretVault.PutAsync(
                new StoreSecretRequest(
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    actorId,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    secret,
                    "managed-codex-rotate",
                    expiresAt,
                    requestedRef),
                mutationCt)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await CompensateUnadoptedWithinOutcomeAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                outcomeDeadline).ConfigureAwait(false);
            throw Failure(
                "managed_credential_vault_store_failed",
                "The managed Codex credential could not be rotated securely.");
        }

        SecretReference newReference;
        try
        {
            newReference = ValidateStoredReference(stored.Reference, requestedRef, actorId, expiresAt);
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            await CompensateUnadoptedWithinOutcomeAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                outcomeDeadline).ConfigureAwait(false);
            throw;
        }

        var descriptor = BuildDescriptor(
            owner,
            persistedKey.Id,
            newReference,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId,
            expiresAt);
        DispatchAdmission admission;
        try
        {
            using var recording = outcomeDeadline.BeginRecording();
            admission = await _commandPort.CommitRotatedAsync(
                current.Credential.ApiKeyId,
                descriptor,
                BuildPreviousCredentialCleanup(
                    current.Credential,
                    descriptor),
                recording.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            throw Failure(
                "managed_credential_persistence_pending",
                "Managed Codex credential persistence is pending reconciliation.");
        }
        if (!admission.Accepted)
        {
            throw Failure(
                "managed_credential_persistence_pending",
                "Managed Codex credential persistence is pending reconciliation.");
        }

        return Result("rotation_accepted", actorId, persistedKey.Id, expiresAt, admission);
    }

    public async Task<ManagedCodexCredentialMutationResult> RevokeAsync(
        string bearerToken,
        string authenticatedUserId,
        CancellationToken ct = default)
    {
        var owner = await ResolveOwnerAsync(bearerToken, authenticatedUserId, requireEligibility: false, ct);
        await using var mutation = await BeginManualMutationAsync(owner, ct)
            .ConfigureAwait(false);
        var current = await RequireActiveAsync(owner, mutation.PreMutationToken)
            .ConfigureAwait(false);
        mutation.PreMutationToken.ThrowIfCancellationRequested();
        if (!await TryRetryPendingCleanupAsync(
                bearerToken,
                owner,
                current.PendingRevocations,
                mutation.PrimaryToken).ConfigureAwait(false))
        {
            throw CleanupPending();
        }
        var credential = current.Credential;
        ValidateActiveDescriptor(credential, owner, _timeProvider.GetUtcNow(), requireFutureExpiry: false);

        ct.ThrowIfCancellationRequested();
        mutation.PrimaryToken.ThrowIfCancellationRequested();
        using var completion = mutation.OutcomeDeadline.BeginCompensation();
        var mutationCt = completion.Token;
        var vaultPending = !await TryRevokeVaultAsync(
            credential.SecretReference.Ref,
            credential.SecretReference.OwnerScopeKey,
            "managed-codex-revoke",
            mutationCt).ConfigureAwait(false);
        var nyxIdPending = !await TryDeleteNyxIdKeyAsync(bearerToken, credential.ApiKeyId, mutationCt)
            .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var cleanup = new ManagedCodexCredentialCleanup
        {
            ApiKeyId = credential.ApiKeyId,
            SecretRef = credential.SecretReference.Ref,
            NyxIdPending = nyxIdPending,
            VaultPending = vaultPending,
            RequestedAt = Timestamp.FromDateTimeOffset(now),
        };
        DispatchAdmission admission;
        try
        {
            using var recording = mutation.OutcomeDeadline.BeginRecording();
            admission = await _commandPort.CommitRevokedAsync(
                owner,
                credential.ApiKeyId,
                cleanup,
                now,
                recording.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                "managed_credential_command_failed",
                "The managed Codex revocation could not be submitted for persistence.");
        }
        if (!admission.Accepted)
            throw Failure("managed_credential_command_rejected", "The managed Codex revocation was not accepted for persistence.");

        return Result(
            "revocation_accepted",
            ManagedCodexCredentialActorIdentity.From(owner),
            credential.ApiKeyId,
            credential.ExpiresAt?.ToDateTimeOffset() ?? now,
            admission);
    }

    private async Task<ReadinessRepairPlan> SelectReadinessRepairAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor? projected,
        CancellationToken ct)
    {
        var eligibility = await _catalogResolver.ResolveAsync(_nyxIdPort, bearerToken, ct)
            .ConfigureAwait(false);
        var activeKeys = await GetActiveManagedKeysAsync(bearerToken, ct)
            .ConfigureAwait(false);
        var current = projected?.Clone();
        var currentCanBeReplaced = IsCommandableCurrent(current, owner);
        if (currentCanBeReplaced)
        {
            var currentCredential = current!;
            var projectedMatches = activeKeys
                .Where(key => string.Equals(
                    key.Id,
                    currentCredential.ApiKeyId,
                    StringComparison.Ordinal))
                .ToArray();
            if (projectedMatches.Length == 1)
            {
                var projectedRemote = projectedMatches[0];
                if (IsCurrentReconciliationCandidate(
                        currentCredential,
                        owner,
                        _timeProvider.GetUtcNow()) &&
                    IsRecoverableRemoteKey(projectedRemote, eligibility) &&
                    HasMatchingExpiry(currentCredential, projectedRemote) &&
                    await TryValidateCurrentVaultReferenceAsync(
                        owner,
                        currentCredential,
                        projectedRemote,
                        ct).ConfigureAwait(false))
                {
                    var exactPolicy = HasExactApiKeyPolicy(projectedRemote, eligibility);
                    var exactDescriptor =
                        IsReady(currentCredential, owner, _timeProvider.GetUtcNow()) &&
                        string.Equals(
                            currentCredential.ChronoSandboxUserServiceId,
                            eligibility.ChronoSandboxUserServiceId,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            currentCredential.ChronoLlmUserServiceId,
                            eligibility.ChronoLlmUserServiceId,
                            StringComparison.Ordinal);
                    var obsoleteApiKeyIds = activeKeys
                        .Where(key => !string.Equals(
                            key.Id,
                            currentCredential.ApiKeyId,
                            StringComparison.Ordinal))
                        .Select(static key => key.Id)
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Select(static value => value.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray();
                    return exactPolicy &&
                           exactDescriptor &&
                           obsoleteApiKeyIds.Length == 0
                        ? new ReadinessRepairPlan(
                            ReadinessRepairKind.None,
                            currentCredential,
                            eligibility,
                            projectedRemote,
                            currentCredential.SecretReference.Clone(),
                            false,
                            [])
                        : new ReadinessRepairPlan(
                            ReadinessRepairKind.ReconcileCurrent,
                            currentCredential,
                            eligibility,
                            projectedRemote,
                            currentCredential.SecretReference.Clone(),
                            !exactPolicy,
                            obsoleteApiKeyIds);
                }
            }

            if (activeKeys.Count > 1)
            {
                return ReplacementPlan(
                    currentCredential,
                    eligibility,
                    activeKeys.Select(static key => key.Id));
            }

            var soleRemote = activeKeys.SingleOrDefault();
            if (soleRemote is null)
                return ReplacementPlan(currentCredential, eligibility, []);
            var adoption = await TrySelectRemoteAdoptionAsync(
                currentCredential,
                soleRemote,
                eligibility,
                owner,
                ct).ConfigureAwait(false);
            return adoption ??
                   ReplacementPlan(currentCredential, eligibility, [soleRemote.Id]);
        }

        if (activeKeys.Count > 1)
        {
            return ReplacementPlan(
                current,
                eligibility,
                activeKeys.Select(static key => key.Id));
        }

        var remote = activeKeys.SingleOrDefault();
        if (remote is not null)
        {
            var adoption = await TrySelectRemoteAdoptionAsync(
                current,
                remote,
                eligibility,
                owner,
                ct).ConfigureAwait(false);
            return adoption ?? ReplacementPlan(current, eligibility, [remote.Id]);
        }

        return ReplacementPlan(current, eligibility, []);
    }

    private async Task<ReadinessRepairPlan?> TrySelectRemoteAdoptionAsync(
        ManagedCodexCredentialDescriptor? current,
        ManagedCodexNyxIdApiKey remote,
        ManagedCodexNyxIdEligibility eligibility,
        ExternalSubjectRef owner,
        CancellationToken ct)
    {
        if (!IsRecoverableRemoteKey(remote, eligibility))
            return null;

        var reference = await TryResolveIssuedReferenceAsync(owner, remote, ct)
            .ConfigureAwait(false);
        if (current?.SecretReference is not null &&
            string.Equals(current.ApiKeyId, remote.Id, StringComparison.Ordinal) &&
            string.Equals(
                current.SecretReference.Ref,
                reference?.Ref,
                StringComparison.Ordinal))
        {
            return null;
        }

        return reference is null
            ? null
            : new ReadinessRepairPlan(
                ReadinessRepairKind.AdoptRemote,
                current,
                eligibility,
                remote,
                reference,
                !HasExactApiKeyPolicy(remote, eligibility),
                []);
    }

    private static ReadinessRepairPlan ReplacementPlan(
        ManagedCodexCredentialDescriptor? current,
        ManagedCodexNyxIdEligibility eligibility,
        IEnumerable<string> apiKeyIdsToRevoke) =>
        new(
            ReadinessRepairKind.Replace,
            current,
            eligibility,
            null,
            null,
            false,
            apiKeyIdsToRevoke
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());

    private async Task<ReadinessRepairOutcome> ExecuteReadinessRepairAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        ReadinessRepairPlan repair,
        OutcomeDeadline outcomeDeadline,
        CancellationToken ct)
    {
        ReadinessRepairOutcome outcome;
        switch (repair.Kind)
        {
            case ReadinessRepairKind.ReconcileCurrent:
                var reconciled = await ReconcilePolicyAsync(
                    bearerToken,
                    owner,
                    repair.Current!,
                    repair.Remote!,
                    repair.Eligibility,
                    ct).ConfigureAwait(false);
                await CleanupObsoleteApiKeysAsync(
                    bearerToken,
                    owner,
                    repair.ApiKeyIdsToRevoke,
                    repair.Current,
                    outcomeDeadline,
                    ct)
                    .ConfigureAwait(false);
                outcome = new ReadinessRepairOutcome(reconciled, null);
                break;
            case ReadinessRepairKind.AdoptRemote:
                outcome = await AdoptRemoteCredentialAsync(
                        bearerToken,
                        owner,
                        repair,
                        ct)
                    .ConfigureAwait(false);
                break;
            case ReadinessRepairKind.Replace:
                outcome = await ReplaceCredentialAsync(
                        bearerToken,
                        owner,
                        repair,
                        outcomeDeadline,
                        ct)
                    .ConfigureAwait(false);
                break;
            case ReadinessRepairKind.None:
                outcome = new ReadinessRepairOutcome(repair.Current!.Clone(), null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(repair));
        }

        _ = await ConfirmReadinessForReconciliationAsync(
                owner,
                outcome.ExpectedCredential,
                ManagedCodexCredentialReadinessEvidence.RemoteValidated,
                ct)
            .ConfigureAwait(false);
        return outcome;
    }

    private async Task<ReadinessRepairOutcome> AdoptRemoteCredentialAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        ReadinessRepairPlan repair,
        CancellationToken ct)
    {
        var remote = repair.Remote!;
        if (repair.UpdateRemotePolicy)
        {
            await _nyxIdPort.UpdateApiKeyPolicyAsync(
                bearerToken,
                remote.Id,
                PolicyUpdateRequest(repair.Eligibility),
                ct).ConfigureAwait(false);
            remote = await RequirePersistedIssuedKeyAsync(
                bearerToken,
                remote.Id,
                repair.Eligibility,
                remote.ExpiresAt!.Value,
                ct).ConfigureAwait(false);
        }
        else
        {
            ValidatePersistedKey(remote, repair.Eligibility, remote.ExpiresAt);
        }

        var descriptor = BuildDescriptor(
            owner,
            remote.Id,
            repair.RemoteReference!,
            repair.Eligibility.ChronoSandboxUserServiceId,
            repair.Eligibility.ChronoLlmUserServiceId,
            remote.ExpiresAt!.Value);
        if (IsCommandableCurrent(repair.Current, owner))
        {
            _ = await CommitRotatedForReconciliationAsync(
                repair.Current!,
                descriptor,
                ct).ConfigureAwait(false);
            return new ReadinessRepairOutcome(
                descriptor,
                repair.Current!.Clone());
        }

        _ = await CommitProvisionedForReconciliationAsync(descriptor, ct)
            .ConfigureAwait(false);
        return new ReadinessRepairOutcome(descriptor, null);
    }

    private async Task<ReadinessRepairOutcome> ReplaceCredentialAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        ReadinessRepairPlan repair,
        OutcomeDeadline outcomeDeadline,
        CancellationToken ct)
    {
        await CleanupObsoleteApiKeysAsync(
            bearerToken,
            owner,
            repair.ApiKeyIdsToRevoke,
            repair.Current,
            outcomeDeadline,
            ct).ConfigureAwait(false);

        var descriptor = await CreateFreshCredentialDescriptorAsync(
            bearerToken,
            owner,
            repair.Eligibility,
            outcomeDeadline,
            ct).ConfigureAwait(false);
        if (IsCommandableCurrent(repair.Current, owner))
        {
            _ = await CommitRotatedForReconciliationAsync(
                repair.Current!,
                descriptor,
                ct).ConfigureAwait(false);
        }
        else
        {
            _ = await CommitProvisionedForReconciliationAsync(descriptor, ct)
                .ConfigureAwait(false);
        }

        return new ReadinessRepairOutcome(
            descriptor,
            IsCommandableCurrent(repair.Current, owner)
                ? repair.Current!.Clone()
                : null);
    }

    private async Task CleanupObsoleteApiKeysAsync(
            string bearerToken,
            ExternalSubjectRef owner,
            IReadOnlyList<string> apiKeyIds,
            ManagedCodexCredentialDescriptor? currentCredential,
            OutcomeDeadline outcomeDeadline,
            CancellationToken ct)
    {
        var ownerScopeKey = ManagedCodexCredentialActorIdentity.From(owner);
        var currentApiKeyId = IsCommandableCurrent(currentCredential, owner)
            ? currentCredential!.ApiKeyId.Trim()
            : null;
        foreach (var apiKeyId in apiKeyIds)
        {
            var isCurrentCredential = string.Equals(
                apiKeyId,
                currentApiKeyId,
                StringComparison.Ordinal);
            if (isCurrentCredential)
                continue;

            var secretRef = SecretRefFor(ownerScopeKey, apiKeyId);
            var cleanup = new ManagedCodexCredentialCleanup
            {
                ApiKeyId = apiKeyId,
                SecretRef = secretRef,
                NyxIdPending = true,
                VaultPending = true,
                RequestedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            };
            try
            {
                cleanup.NyxIdPending = !await TryDeleteNyxIdKeyAsync(
                        bearerToken,
                        apiKeyId,
                        ct)
                    .ConfigureAwait(false);
                cleanup.VaultPending =
                    !await TryRevokeVaultAsync(
                            secretRef,
                            ownerScopeKey,
                            "managed-codex-orphan-cleanup",
                            ct)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _ = await QueueCleanupOutcomeAsync(
                        owner,
                        cleanup,
                        outcomeDeadline)
                    .ConfigureAwait(false);
                throw;
            }

            if ((cleanup.NyxIdPending || cleanup.VaultPending) &&
                !await QueueCleanupOutcomeAsync(
                        owner,
                        cleanup,
                        outcomeDeadline)
                    .ConfigureAwait(false))
            {
                throw PersistencePending();
            }
        }
    }

    private async Task<ManagedCodexCredentialDescriptor> CreateFreshCredentialDescriptorAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        ManagedCodexNyxIdEligibility eligibility,
        OutcomeDeadline outcomeDeadline,
        CancellationToken ct)
    {
        var requestedExpiresAt = _timeProvider.GetUtcNow()
            .AddDays(_options.CredentialLifetimeDays);
        var issued = await _nyxIdPort.CreateApiKeyAsync(
            bearerToken,
            IssueRequest(eligibility, requestedExpiresAt),
            ct).ConfigureAwait(false);
        ManagedCodexNyxIdApiKey persistedKey;
        try
        {
            ValidateIssuedKey(issued.Key, eligibility);
            persistedKey = await RequirePersistedIssuedKeyAsync(
                bearerToken,
                issued.Key.Id,
                eligibility,
                requestedExpiresAt,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(issued.Key.Id))
            {
                _ = await CompensateRejectedIssuanceWithinOutcomeAsync(
                    bearerToken,
                    owner,
                    issued.Key.Id,
                    outcomeDeadline).ConfigureAwait(false);
            }
            throw;
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            if (!string.IsNullOrWhiteSpace(issued.Key.Id))
            {
                _ = await CompensateRejectedIssuanceWithinOutcomeAsync(
                    bearerToken,
                    owner,
                    issued.Key.Id,
                    outcomeDeadline).ConfigureAwait(false);
            }
            throw;
        }

        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        var expiresAt = persistedKey.ExpiresAt!.Value;
        var requestedRef = SecretRefFor(actorId, persistedKey.Id);
        StoreSecretResult stored;
        try
        {
            stored = await issued.Secret.UseAsync(secret => _secretVault.PutAsync(
                new StoreSecretRequest(
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    actorId,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    secret,
                    "managed-codex-readiness",
                    expiresAt,
                    requestedRef),
                ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CompensateUnadoptedWithinOutcomeAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                outcomeDeadline).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await CompensateUnadoptedWithinOutcomeAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                outcomeDeadline).ConfigureAwait(false);
            throw Failure(
                "managed_credential_vault_store_failed",
                "The managed Codex credential could not be stored securely.");
        }

        SecretReference reference;
        try
        {
            reference = ValidateStoredReference(
                stored.Reference,
                requestedRef,
                actorId,
                expiresAt);
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            await CompensateUnadoptedWithinOutcomeAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                outcomeDeadline).ConfigureAwait(false);
            throw;
        }

        return BuildDescriptor(
            owner,
            persistedKey.Id,
            reference,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId,
            expiresAt);
    }

    private async Task<bool> TryValidateCurrentVaultReferenceAsync(
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor current,
        ManagedCodexNyxIdApiKey remote,
        CancellationToken ct)
    {
        var ownerScopeKey = ManagedCodexCredentialActorIdentity.From(owner);
        var reference = current.SecretReference;
        if (reference is null ||
            remote.ExpiresAt is null ||
            !ReferenceMatches(
                reference,
                reference.Ref,
                ownerScopeKey,
                remote.ExpiresAt.Value))
        {
            return false;
        }

        try
        {
            var resolved = await _secretVault.ResolveAsync(
                new ResolveSecretRequest(
                    reference.Ref,
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    ownerScopeKey,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    "managed-codex-readiness-validate"),
                ct).ConfigureAwait(false);
            if (resolved.FailureReason is
                SecretResolutionFailureReason.Unauthorized or
                SecretResolutionFailureReason.AuthenticationFailed or
                SecretResolutionFailureReason.KeyringMismatch or
                SecretResolutionFailureReason.UnsupportedAlgorithm)
            {
                throw Failure(
                    "managed_credential_vault_unavailable",
                    "The managed Codex credential Vault is unavailable; retry reconciliation later.");
            }

            return resolved.Resolved &&
                   !string.IsNullOrWhiteSpace(resolved.Secret) &&
                   resolved.Reference is not null &&
                   resolved.Reference.Equals(reference);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Managed Codex Vault reference validation failed for API key {ApiKeyId}; readiness repair stopped.",
                current.ApiKeyId);
            throw Failure(
                "managed_credential_vault_unavailable",
                "The managed Codex credential Vault is unavailable; retry reconciliation later.");
        }
    }

    private static bool IsCommandableCurrent(
        ManagedCodexCredentialDescriptor? current,
        ExternalSubjectRef owner)
    {
        if (current?.Owner is null ||
            current.Status != ManagedCodexCredentialStatus.Active ||
            string.IsNullOrWhiteSpace(current.ApiKeyId))
        {
            return false;
        }

        try
        {
            return string.Equals(
                ManagedCodexCredentialActorIdentity.From(current.Owner),
                ManagedCodexCredentialActorIdentity.From(owner),
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsCurrentReconciliationCandidate(
        ManagedCodexCredentialDescriptor current,
        ExternalSubjectRef owner,
        DateTimeOffset now)
    {
        if (!IsCommandableCurrent(current, owner) ||
            current.SecretReference is null ||
            current.ExpiresAt is null)
        {
            return false;
        }

        try
        {
            var expiresAt = current.ExpiresAt.ToDateTimeOffset();
            return expiresAt > now &&
                   !string.IsNullOrWhiteSpace(current.SecretReference.Ref) &&
                   current.SecretReference.ExpiresAtUnixMs ==
                   expiresAt.ToUnixTimeMilliseconds();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool HasMatchingExpiry(
        ManagedCodexCredentialDescriptor current,
        ManagedCodexNyxIdApiKey remote)
    {
        if (current.ExpiresAt is null || remote.ExpiresAt is null)
            return false;

        try
        {
            return current.ExpiresAt.ToDateTimeOffset().ToUnixTimeMilliseconds() ==
                   remote.ExpiresAt.Value.ToUnixTimeMilliseconds();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private bool IsRecoverableRemoteKey(
        ManagedCodexNyxIdApiKey key,
        ManagedCodexNyxIdEligibility eligibility)
    {
        if (string.IsNullOrWhiteSpace(key.Id) ||
            !string.Equals(key.Name, CredentialName, StringComparison.Ordinal) ||
            !key.IsActive ||
            key.ExpiresAt is null ||
            key.ExpiresAt.Value <= _timeProvider.GetUtcNow() ||
            !string.Equals(key.Scopes, "proxy", StringComparison.Ordinal) ||
            !string.Equals(key.Platform, "codex", StringComparison.Ordinal) ||
            key.AllowAllServices ||
            key.AllowAllNodes ||
            key.AllowedNodeIds is not { Count: 0 })
        {
            return false;
        }

        if (HasExactServiceIds(
                key.AllowedServiceIds,
                eligibility.ChronoSandboxUserServiceId,
                eligibility.ChronoLlmUserServiceId))
        {
            return true;
        }

        return key.AllowedServiceIds is { Count: 1 } &&
               string.Equals(
                   key.AllowedServiceIds[0],
                   eligibility.ChronoSandboxUserServiceId,
                   StringComparison.Ordinal);
    }

    private void ValidateReadinessRequest(
        ExternalSubjectRef owner,
        ManagedCodexCredentialReadinessMode mode)
    {
        _ = ManagedCodexCredentialActorIdentity.From(owner);
        if (!System.Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (!_options.IsEligible(owner.ExternalUserId))
        {
            throw Failure(
                "managed_feature_not_enabled",
                "Managed Codex is not enabled for this user.");
        }
    }

    private async Task VerifyBearerOwnerAsync(
        string bearerToken,
        string expectedUserId,
        CancellationToken ct)
    {
        var currentUserId = await _nyxIdPort.GetCurrentUserIdAsync(bearerToken, ct)
            .ConfigureAwait(false);
        if (!string.Equals(
                currentUserId?.Trim(),
                expectedUserId,
                StringComparison.Ordinal))
        {
            throw Failure(
                "nyxid_identity_mismatch",
                "The authenticated identity does not own the supplied NyxID bearer.");
        }
    }

    private bool CanReturnNormalReady(
        ManagedCodexCredentialSnapshot? snapshot,
        ExternalSubjectRef owner,
        string? bearerToken,
        ManagedCodexCredentialReadinessMode mode) =>
        mode == ManagedCodexCredentialReadinessMode.Normal &&
        IsReady(snapshot?.Credential, owner, _timeProvider.GetUtcNow()) &&
        (snapshot!.PendingRevocations.Count == 0 ||
         string.IsNullOrWhiteSpace(bearerToken));

    private static bool IsReady(
        ManagedCodexCredentialDescriptor? credential,
        ExternalSubjectRef owner,
        DateTimeOffset now)
    {
        if (credential?.Owner is null ||
            credential.SecretReference is null ||
            string.IsNullOrWhiteSpace(credential.ApiKeyId) ||
            string.IsNullOrWhiteSpace(credential.SecretReference.Ref) ||
            credential.Status != ManagedCodexCredentialStatus.Active ||
            credential.ExpiresAt is null ||
            string.IsNullOrWhiteSpace(credential.ChronoSandboxUserServiceId) ||
            string.IsNullOrWhiteSpace(credential.ChronoLlmUserServiceId) ||
            string.Equals(
                credential.ChronoSandboxUserServiceId,
                credential.ChronoLlmUserServiceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                credential.ChronoSandboxServiceSlug,
                ManagedCodexOptions.ChronoSandboxServiceSlug,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var ownerScopeKey = ManagedCodexCredentialActorIdentity.From(owner);
            var expiresAt = credential.ExpiresAt.ToDateTimeOffset();
            return string.Equals(
                       ManagedCodexCredentialActorIdentity.From(credential.Owner),
                       ownerScopeKey,
                       StringComparison.Ordinal) &&
                   expiresAt > now &&
                   ReferenceMatches(
                       credential.SecretReference,
                       credential.SecretReference.Ref,
                       ownerScopeKey,
                       expiresAt);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<ManagedCodexCredentialDescriptor> WaitForReadyAsync(
        IManagedCodexCredentialReadinessObservationLease observation,
        ExternalSubjectRef owner,
        ManagedCodexCredentialReadinessMode mode,
        CancellationToken ct,
        long minimumStateVersion = long.MinValue,
        ManagedCodexCredentialDescriptor? expectedCredential = null)
    {
        await foreach (var snapshot in observation.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (snapshot.StateVersion > minimumStateVersion &&
                (expectedCredential is null ||
                 snapshot.Credential?.Equals(expectedCredential) == true) &&
                HasSufficientReadinessEvidence(snapshot.ReadinessEvidence, mode) &&
                IsReady(snapshot.Credential, owner, _timeProvider.GetUtcNow()))
            {
                return snapshot.Credential!.Clone();
            }
        }

        throw CommitTimeout();
    }

    private static bool HasSufficientReadinessEvidence(
        ManagedCodexCredentialReadinessEvidence evidence,
        ManagedCodexCredentialReadinessMode mode) =>
        mode switch
        {
            ManagedCodexCredentialReadinessMode.Normal =>
                evidence is
                    ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed or
                    ManagedCodexCredentialReadinessEvidence.RemoteValidated,
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation =>
                evidence == ManagedCodexCredentialReadinessEvidence.RemoteValidated,
            _ => false,
        };

    private void LogPendingCleanup(
        IReadOnlyList<ManagedCodexCredentialCleanup> pending)
    {
        var apiKeyIds = pending
            .Select(static cleanup => cleanup.ApiKeyId?.Trim())
            .Where(static apiKeyId => !string.IsNullOrWhiteSpace(apiKeyId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        _logger.LogWarning(
            "Managed Codex obsolete credential cleanup remains pending for API keys {ApiKeyIds}; the current committed credential remains ready.",
            string.Join(",", apiKeyIds));
    }

    private static ManagedCodexCredentialLifecycleException CommitTimeout() =>
        Failure(
            "managed_credential_commit_timeout",
            "Managed Codex credential readiness was not committed within the allowed time.");

    private static ManagedCodexCredentialLifecycleException CleanupPending() =>
        Failure(
            "managed_credential_cleanup_pending",
            "Managed Codex credential cleanup is still pending; retry later.");

    private async Task<ExternalSubjectRef> ResolveOwnerAsync(
        string bearerToken,
        string authenticatedUserId,
        bool requireEligibility,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedUserId);
        var currentUserId = await _nyxIdPort.GetCurrentUserIdAsync(bearerToken.Trim(), ct)
            .ConfigureAwait(false);
        if (!string.Equals(currentUserId, authenticatedUserId.Trim(), StringComparison.Ordinal))
            throw Failure("nyxid_identity_mismatch", "The authenticated identity does not own the supplied NyxID bearer.");
        if (requireEligibility && !_options.IsEligible(currentUserId))
            throw Failure("managed_feature_not_enabled", "Managed Codex is not enabled for this user.");

        return new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = currentUserId,
        };
    }

    private async Task<ManagedCodexCredentialSnapshot> RequireActiveAsync(
        ExternalSubjectRef owner,
        CancellationToken ct)
    {
        var snapshot = await _queryPort.ResolveAsync(owner, ct).ConfigureAwait(false);
        if (snapshot?.Credential is null || snapshot.Credential.Status != ManagedCodexCredentialStatus.Active)
            throw Failure("managed_credential_not_provisioned", "Managed Codex is not provisioned for this user.");
        return snapshot;
    }

    private async ValueTask<ManualMutationScope> BeginManualMutationAsync(
        ExternalSubjectRef owner,
        CancellationToken requestCt)
    {
        var outcomeDeadline = BeginOutcomeDeadline(_timeProvider.GetUtcNow());
        var lease = await AcquireMutationLeaseAsync(
            owner,
            outcomeDeadline,
            requestCt).ConfigureAwait(false);
        var primary = outcomeDeadline.BeginPrimary();
        var preMutationWait = CancellationTokenSource.CreateLinkedTokenSource(
            requestCt,
            primary.Token);
        return new ManualMutationScope(
            outcomeDeadline,
            lease,
            primary,
            preMutationWait);
    }

    private async ValueTask<IManagedCodexCredentialMutationLeaseHandle> AcquireMutationLeaseAsync(
        ExternalSubjectRef owner,
        OutcomeDeadline outcomeDeadline,
        CancellationToken requestCt)
    {
        var ownerKey = ManagedCodexCredentialActorIdentity.From(owner);
        using var acquisition = outcomeDeadline.BeginPrimary();
        using var acquisitionWait =
            CancellationTokenSource.CreateLinkedTokenSource(
                requestCt,
                acquisition.Token);
        IManagedCodexCredentialMutationLeaseHandle? lease;
        try
        {
            lease = await _mutationLease.TryAcquireAsync(
                    ownerKey,
                    acquisitionWait.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!requestCt.IsCancellationRequested)
        {
            throw CommitTimeout();
        }

        if (requestCt.IsCancellationRequested || acquisition.IsCancellationRequested)
        {
            if (lease is not null)
                await lease.DisposeAsync().ConfigureAwait(false);
            requestCt.ThrowIfCancellationRequested();
            throw CommitTimeout();
        }

        return lease ??
               throw Failure(
                   "managed_credential_mutation_in_progress",
                   "Another managed Codex credential mutation is already in progress for this user.");
    }

    private OutcomeDeadline BeginOutcomeDeadline(DateTimeOffset acquisitionAnchor)
    {
        var recordingExpiresAt = acquisitionAnchor
            .AddSeconds(_options.MutationLeaseSeconds)
            .Subtract(TimeSpan.FromSeconds(
                ManagedCodexOptions.MutationLeaseSafetySeconds));
        return new OutcomeDeadline(
            _timeProvider,
            acquisitionAnchor.AddSeconds(
                _options.MutationCompletionSeconds),
            recordingExpiresAt.Subtract(TimeSpan.FromSeconds(
                ManagedCodexOptions.MutationRecordingReserveSeconds)),
            recordingExpiresAt);
    }

    private static ManagedCodexNyxIdApiKeyIssueRequest IssueRequest(
        ManagedCodexNyxIdEligibility eligibility,
        DateTimeOffset expiresAt) =>
        new(
            CredentialName,
            "Aevatar managed codex_exec invocation credential",
            "proxy",
            "codex",
            false,
            [
                eligibility.ChronoSandboxUserServiceId,
                eligibility.ChronoLlmUserServiceId,
            ],
            false,
            [],
            expiresAt);

    private static ManagedCodexNyxIdApiKeyPolicyUpdateRequest PolicyUpdateRequest(
        ManagedCodexNyxIdEligibility eligibility) =>
        new(
            "proxy",
            "codex",
            false,
            [
                eligibility.ChronoSandboxUserServiceId,
                eligibility.ChronoLlmUserServiceId,
            ],
            false,
            []);

    private async Task<IReadOnlyList<ManagedCodexNyxIdApiKey>> GetActiveManagedKeysAsync(
        string bearerToken,
        CancellationToken ct)
    {
        var keys = await _nyxIdPort.ListApiKeysAsync(bearerToken, ct).ConfigureAwait(false);
        return keys
            .Where(static key =>
                key.IsActive &&
                string.Equals(key.Name, CredentialName, StringComparison.Ordinal))
            .ToArray();
    }

    private static void EnsureAtMostOneActiveKey(
        IReadOnlyList<ManagedCodexNyxIdApiKey> activeKeys)
    {
        if (activeKeys.Count <= 1)
            return;

        throw Failure(
            "managed_credential_untracked_key_exists",
            "Multiple active managed Codex keys exist in NyxID and must be reconciled.");
    }

    private async Task<ManagedCodexCredentialMutationResult?> TryReconcileProvisionAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        ManagedCodexNyxIdApiKey activeKey,
        ManagedCodexNyxIdEligibility eligibility,
        OutcomeDeadline outcomeDeadline,
        CancellationToken ct)
    {
        try
        {
            ValidatePersistedKey(activeKey, eligibility, expectedExpiresAt: null);
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            _ = await CompensateRejectedIssuanceWithinOutcomeAsync(
                bearerToken,
                owner,
                activeKey.Id,
                outcomeDeadline).ConfigureAwait(false);
            throw;
        }

        var reference = await TryResolveIssuedReferenceAsync(owner, activeKey, ct)
            .ConfigureAwait(false);
        if (reference is null)
        {
            if (!await CompensateRejectedIssuanceWithinOutcomeAsync(
                    bearerToken,
                    owner,
                    activeKey.Id,
                    outcomeDeadline).ConfigureAwait(false))
            {
                throw Failure(
                    "managed_credential_cleanup_pending",
                    "The untracked managed Codex key could not be revoked; retry later.");
            }
            return null;
        }

        var descriptor = BuildDescriptor(
            owner,
            activeKey.Id,
            reference,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId,
            activeKey.ExpiresAt!.Value);
        using var recording = outcomeDeadline.BeginRecording();
        var admission = await CommitProvisionedForReconciliationAsync(
                descriptor,
                recording.Token)
            .ConfigureAwait(false);
        return Result(
            "provisioning_reconciliation_accepted",
            ManagedCodexCredentialActorIdentity.From(owner),
            activeKey.Id,
            activeKey.ExpiresAt.Value,
            admission);
    }

    private async Task<ManagedCodexCredentialMutationResult?> TryReconcileRotationAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor current,
        ManagedCodexNyxIdApiKey activeKey,
        ManagedCodexNyxIdEligibility eligibility,
        OutcomeDeadline outcomeDeadline,
        CancellationToken ct)
    {
        try
        {
            ValidatePersistedKey(activeKey, eligibility, expectedExpiresAt: null);
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            _ = await CompensateRejectedIssuanceWithinOutcomeAsync(
                bearerToken,
                owner,
                activeKey.Id,
                outcomeDeadline).ConfigureAwait(false);
            throw;
        }

        var reference = await TryResolveIssuedReferenceAsync(owner, activeKey, ct)
            .ConfigureAwait(false);
        if (reference is null)
        {
            if (!await CompensateRejectedIssuanceWithinOutcomeAsync(
                    bearerToken,
                    owner,
                    activeKey.Id,
                    outcomeDeadline).ConfigureAwait(false))
            {
                throw Failure(
                    "managed_credential_cleanup_pending",
                    "The unrecoverable managed Codex key could not be revoked; retry later.");
            }
            return null;
        }

        var descriptor = BuildDescriptor(
            owner,
            activeKey.Id,
            reference,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId,
            activeKey.ExpiresAt!.Value);
        using var recording = outcomeDeadline.BeginRecording();
        var admission = await CommitRotatedForReconciliationAsync(
            current,
            descriptor,
            recording.Token).ConfigureAwait(false);
        return Result(
            "rotation_reconciliation_accepted",
            ManagedCodexCredentialActorIdentity.From(owner),
            activeKey.Id,
            activeKey.ExpiresAt.Value,
            admission);
    }

    private async Task<ManagedCodexCredentialDescriptor> ReconcilePolicyAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor current,
        ManagedCodexNyxIdApiKey remote,
        ManagedCodexNyxIdEligibility eligibility,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(eligibility);
        ValidateActiveDescriptor(current, owner, _timeProvider.GetUtcNow());
        var currentApiKeyId = current.ApiKeyId.Trim();
        if (!string.Equals(currentApiKeyId, remote.Id?.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                "managed_api_key_update_invalid",
                "The managed Codex key selected for policy repair does not match the current credential.");
        }

        if (!HasExactApiKeyPolicy(remote, eligibility))
        {
            await _nyxIdPort.UpdateApiKeyPolicyAsync(
                bearerToken,
                currentApiKeyId,
                PolicyUpdateRequest(eligibility),
                ct).ConfigureAwait(false);
        }

        var keys = await _nyxIdPort.ListApiKeysAsync(bearerToken, ct).ConfigureAwait(false);
        var matches = keys
            .Where(key => string.Equals(key.Id, currentApiKeyId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw Failure(
                "managed_api_key_update_invalid",
                "NyxID did not persist the repaired managed Codex key.");
        }

        var persisted = matches[0];
        ValidatePersistedKey(persisted, eligibility, expectedExpiresAt: null);
        var reference = await RequireCurrentVaultReferenceAsync(
            owner,
            current,
            persisted,
            ct).ConfigureAwait(false);
        var descriptor = BuildDescriptor(
            owner,
            currentApiKeyId,
            reference,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId,
            persisted.ExpiresAt!.Value);
        await CommitPolicyReconciledForReconciliationAsync(
            currentApiKeyId,
            descriptor,
            ct).ConfigureAwait(false);
        return descriptor;
    }

    private async Task<DispatchAdmission> CommitProvisionedForReconciliationAsync(
        ManagedCodexCredentialDescriptor descriptor,
        CancellationToken ct)
    {
        try
        {
            var admission = await _commandPort.CommitProvisionedAsync(descriptor, ct)
                .ConfigureAwait(false);
            return admission.Accepted
                ? admission
                : throw PersistencePending();
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            throw;
        }
        catch (Exception)
        {
            throw PersistencePending();
        }
    }

    private async Task<DispatchAdmission> CommitRotatedForReconciliationAsync(
        ManagedCodexCredentialDescriptor previousCredential,
        ManagedCodexCredentialDescriptor descriptor,
        CancellationToken ct)
    {
        try
        {
            var admission = await _commandPort.CommitRotatedAsync(
                previousCredential.ApiKeyId,
                descriptor,
                BuildPreviousCredentialCleanup(
                    previousCredential,
                    descriptor),
                ct).ConfigureAwait(false);
            return admission.Accepted
                ? admission
                : throw PersistencePending();
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            throw;
        }
        catch (Exception)
        {
            throw PersistencePending();
        }
    }

    private async Task<DispatchAdmission> CommitPolicyReconciledForReconciliationAsync(
        string expectedApiKeyId,
        ManagedCodexCredentialDescriptor descriptor,
        CancellationToken ct)
    {
        try
        {
            var admission = await _commandPort.CommitPolicyReconciledAsync(
                expectedApiKeyId,
                descriptor,
                ct).ConfigureAwait(false);
            return admission.Accepted
                ? admission
                : throw PersistencePending();
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            throw;
        }
        catch (Exception)
        {
            throw PersistencePending();
        }
    }

    private async Task<DispatchAdmission> ConfirmReadinessForReconciliationAsync(
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor expectedCredential,
        ManagedCodexCredentialReadinessEvidence readinessEvidence,
        CancellationToken ct)
    {
        try
        {
            var admission = await _commandPort.ConfirmReadinessAsync(
                owner,
                expectedCredential,
                readinessEvidence,
                ct).ConfigureAwait(false);
            return admission.Accepted
                ? admission
                : throw PersistencePending();
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            throw;
        }
        catch (Exception)
        {
            throw PersistencePending();
        }
    }

    private async Task<ManagedCodexNyxIdApiKey> RequirePersistedIssuedKeyAsync(
        string bearerToken,
        string apiKeyId,
        ManagedCodexNyxIdEligibility eligibility,
        DateTimeOffset expectedExpiresAt,
        CancellationToken ct)
    {
        var keys = await _nyxIdPort.ListApiKeysAsync(bearerToken, ct).ConfigureAwait(false);
        var matches = keys
            .Where(key => string.Equals(key.Id, apiKeyId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw Failure(
                "managed_api_key_issue_invalid",
                "NyxID did not persist the issued managed Codex key.");
        }

        ValidatePersistedKey(matches[0], eligibility, expectedExpiresAt);
        return matches[0];
    }

    private void ValidatePersistedKey(
        ManagedCodexNyxIdApiKey key,
        ManagedCodexNyxIdEligibility eligibility,
        DateTimeOffset? expectedExpiresAt)
    {
        ValidateIssuedKey(key, eligibility);
        if (!key.IsActive ||
            key.ExpiresAt is null ||
            key.ExpiresAt.Value <= _timeProvider.GetUtcNow() ||
            expectedExpiresAt.HasValue &&
            key.ExpiresAt.Value.ToUnixTimeMilliseconds() !=
            expectedExpiresAt.Value.ToUnixTimeMilliseconds())
        {
            throw Failure(
                "managed_api_key_expiry_invalid",
                "NyxID did not persist the required finite managed Codex key expiry.");
        }
    }

    private async Task<SecretReference?> TryResolveIssuedReferenceAsync(
        ExternalSubjectRef owner,
        ManagedCodexNyxIdApiKey key,
        CancellationToken ct)
    {
        var ownerScopeKey = ManagedCodexCredentialActorIdentity.From(owner);
        var secretRef = SecretRefFor(ownerScopeKey, key.Id);
        try
        {
            var resolved = await _secretVault.ResolveAsync(
                new ResolveSecretRequest(
                    secretRef,
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    ownerScopeKey,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    "managed-codex-reconcile"),
                ct).ConfigureAwait(false);
            if (resolved.FailureReason is
                SecretResolutionFailureReason.Unauthorized or
                SecretResolutionFailureReason.AuthenticationFailed or
                SecretResolutionFailureReason.KeyringMismatch or
                SecretResolutionFailureReason.UnsupportedAlgorithm)
            {
                throw Failure(
                    "managed_credential_vault_unavailable",
                    "The managed Codex credential Vault is unavailable; retry reconciliation later.");
            }

            if (!resolved.Resolved ||
                string.IsNullOrWhiteSpace(resolved.Secret) ||
                resolved.Reference is null ||
                !ReferenceMatches(
                    resolved.Reference,
                    secretRef,
                    ownerScopeKey,
                    key.ExpiresAt!.Value))
            {
                return null;
            }
            return resolved.Reference.Clone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Managed Codex Vault reference resolution failed for API key {ApiKeyId}; reconciliation stopped.",
                key.Id);
            throw Failure(
                "managed_credential_vault_unavailable",
                "The managed Codex credential Vault is unavailable; retry reconciliation later.");
        }
    }

    private async Task<SecretReference> RequireCurrentVaultReferenceAsync(
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor current,
        ManagedCodexNyxIdApiKey key,
        CancellationToken ct)
    {
        var ownerScopeKey = ManagedCodexCredentialActorIdentity.From(owner);
        var currentReference = current.SecretReference;
        var expiresAt = key.ExpiresAt!.Value;
        if (currentReference is null ||
            string.IsNullOrWhiteSpace(currentReference.Ref) ||
            current.ExpiresAt is null ||
            current.ExpiresAt.ToDateTimeOffset().ToUnixTimeMilliseconds() !=
            expiresAt.ToUnixTimeMilliseconds() ||
            !ReferenceMatches(
                currentReference,
                currentReference.Ref,
                ownerScopeKey,
                expiresAt))
        {
            throw Failure(
                "managed_credential_vault_reference_invalid",
                "The current managed Codex credential has an invalid Vault reference.");
        }

        try
        {
            var resolved = await _secretVault.ResolveAsync(
                new ResolveSecretRequest(
                    currentReference.Ref,
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    ownerScopeKey,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    "managed-codex-policy-reconcile"),
                ct).ConfigureAwait(false);
            if (!resolved.Resolved ||
                string.IsNullOrWhiteSpace(resolved.Secret) ||
                resolved.Reference is null ||
                !ReferenceMatches(
                    resolved.Reference,
                    currentReference.Ref,
                    ownerScopeKey,
                    expiresAt) ||
                resolved.Reference.Version != currentReference.Version ||
                !string.Equals(
                    resolved.Reference.Fingerprint,
                    currentReference.Fingerprint,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    "managed_credential_vault_reference_invalid",
                    "The current managed Codex credential Vault reference could not be validated.");
            }
            return currentReference.Clone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Managed Codex Vault reference validation failed for API key {ApiKeyId}; policy reconciliation stopped.",
                key.Id);
            throw Failure(
                "managed_credential_vault_unavailable",
                "The managed Codex credential Vault is unavailable; retry reconciliation later.");
        }
    }

    private async Task RetryCleanupAfterReadinessAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        IReadOnlyList<ManagedCodexCredentialCleanup> pendingCleanup,
        OutcomeDeadline outcomeDeadline,
        CancellationToken requestCt)
    {
        if (pendingCleanup.Count == 0)
            return;

        requestCt.ThrowIfCancellationRequested();
        using var cleanupDeadline = outcomeDeadline.BeginCompensation();
        using var cleanupWait =
            CancellationTokenSource.CreateLinkedTokenSource(
                requestCt,
                cleanupDeadline.Token);
        bool completed;
        try
        {
            completed = await TryRetryPendingCleanupAsync(
                    bearerToken,
                    owner,
                    pendingCleanup,
                    cleanupWait.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!requestCt.IsCancellationRequested &&
                  cleanupDeadline.IsCancellationRequested)
        {
            completed = false;
        }

        if (!completed)
            LogPendingCleanup(pendingCleanup);
    }

    private ManagedCodexCredentialCleanup BuildPreviousCredentialCleanup(
        ManagedCodexCredentialDescriptor previous,
        ManagedCodexCredentialDescriptor rotated) =>
        new()
        {
            ApiKeyId = previous.ApiKeyId,
            SecretRef = previous.SecretReference?.Ref ?? string.Empty,
            NyxIdPending = !string.Equals(
                previous.ApiKeyId,
                rotated.ApiKeyId,
                StringComparison.Ordinal),
            VaultPending = !string.Equals(
                previous.SecretReference?.Ref,
                rotated.SecretReference?.Ref,
                StringComparison.Ordinal),
            RequestedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        };

    private static string SecretRefFor(string ownerScopeKey, string apiKeyId)
    {
        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{ownerScopeKey}\n{apiKeyId.Trim()}"));
        return "sec_managed_codex_" + Convert.ToHexStringLower(digest);
    }

    private static bool ReferenceMatches(
        SecretReference reference,
        string expectedRef,
        string expectedOwnerScopeKey,
        DateTimeOffset expectedExpiresAt) =>
        string.Equals(reference.Ref, expectedRef, StringComparison.Ordinal) &&
        string.Equals(
            reference.Purpose,
            CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
            StringComparison.Ordinal) &&
        string.Equals(reference.OwnerScopeKey, expectedOwnerScopeKey, StringComparison.Ordinal) &&
        reference.Version > 0 &&
        !string.IsNullOrWhiteSpace(reference.Fingerprint) &&
        reference.ExpiresAtUnixMs == expectedExpiresAt.ToUnixTimeMilliseconds();

    private static ManagedCodexCredentialLifecycleException PersistencePending() =>
        Failure(
            "managed_credential_persistence_pending",
            "Managed Codex credential persistence is pending reconciliation.");

    private async Task<bool> TryRetryPendingCleanupAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        IReadOnlyList<ManagedCodexCredentialCleanup>? pendingRevocations,
        CancellationToken ct)
    {
        if (pendingRevocations is null || pendingRevocations.Count == 0)
            return true;

        var allCompleted = true;
        var ownerScopeKey = ManagedCodexCredentialActorIdentity.From(owner);
        foreach (var cleanup in pendingRevocations)
        {
            if (string.IsNullOrWhiteSpace(cleanup.ApiKeyId))
            {
                allCompleted = false;
                continue;
            }

            var apiKeyId = cleanup.ApiKeyId.Trim();
            if (cleanup.NyxIdPending)
            {
                var deleted = await TryDeleteNyxIdKeyAsync(bearerToken, apiKeyId, ct)
                    .ConfigureAwait(false);
                if (!deleted || !await CompleteCleanupTrackAsync(
                        owner,
                        apiKeyId,
                        ManagedCodexCredentialCleanupTrack.NyxId,
                        ct).ConfigureAwait(false))
                {
                    allCompleted = false;
                }
            }

            if (cleanup.VaultPending)
            {
                var revoked = !string.IsNullOrWhiteSpace(cleanup.SecretRef) &&
                              await TryRevokeVaultAsync(
                                  cleanup.SecretRef.Trim(),
                                  ownerScopeKey,
                                  "managed-codex-retry-cleanup",
                                  ct).ConfigureAwait(false);
                if (!revoked || !await CompleteCleanupTrackAsync(
                        owner,
                        apiKeyId,
                        ManagedCodexCredentialCleanupTrack.Vault,
                        ct).ConfigureAwait(false))
                {
                    allCompleted = false;
                }
            }
        }

        return allCompleted;
    }

    private async Task<bool> CompleteCleanupTrackAsync(
        ExternalSubjectRef owner,
        string apiKeyId,
        ManagedCodexCredentialCleanupTrack track,
        CancellationToken ct)
    {
        try
        {
            var admission = await _commandPort.CompleteCleanupTrackAsync(
                owner,
                apiKeyId,
                track,
                _timeProvider.GetUtcNow(),
                ct).ConfigureAwait(false);
            return admission.Accepted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task CompensateUnadoptedWithinOutcomeAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        string apiKeyId,
        string secretRef,
        string ownerScopeKey,
        OutcomeDeadline outcomeDeadline)
    {
        using var cleanup = outcomeDeadline.BeginCompensation();
        var nyxIdPending = !await TryDeleteForOutcomeAsync(
            bearerToken,
            apiKeyId,
            cleanup.Token).ConfigureAwait(false);
        var vaultPending = !await TryRevokeVaultForOutcomeAsync(
            secretRef,
            ownerScopeKey,
            "managed-codex-mutation-compensation",
            cleanup.Token).ConfigureAwait(false);
        if (!nyxIdPending && !vaultPending)
            return;

        await QueueCleanupOutcomeAsync(
            owner,
            new ManagedCodexCredentialCleanup
            {
                ApiKeyId = apiKeyId,
                SecretRef = secretRef,
                NyxIdPending = nyxIdPending,
                VaultPending = vaultPending,
                RequestedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            },
            outcomeDeadline).ConfigureAwait(false);
    }

    private async Task<bool> CompensateRejectedIssuanceWithinOutcomeAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        string apiKeyId,
        OutcomeDeadline outcomeDeadline)
    {
        using var cleanup = outcomeDeadline.BeginCompensation();
        if (await TryDeleteForOutcomeAsync(
                bearerToken,
                apiKeyId,
                cleanup.Token).ConfigureAwait(false))
        {
            return true;
        }

        _ = await QueueCleanupOutcomeAsync(
            owner,
            new ManagedCodexCredentialCleanup
            {
                ApiKeyId = apiKeyId,
                NyxIdPending = true,
                VaultPending = false,
                RequestedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            },
            outcomeDeadline).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> TryDeleteForOutcomeAsync(
        string bearerToken,
        string apiKeyId,
        CancellationToken ct)
    {
        try
        {
            return await _nyxIdPort.RevokeApiKeyAsync(bearerToken, apiKeyId, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryRevokeVaultForOutcomeAsync(
        string secretRef,
        string ownerScopeKey,
        string auditReason,
        CancellationToken ct)
    {
        try
        {
            var result = await _secretVault.RevokeAsync(
                new RevokeSecretRequest(
                    secretRef,
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    ownerScopeKey,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    auditReason),
                ct).ConfigureAwait(false);
            return result.Revoked;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> QueueCleanupOutcomeAsync(
        ExternalSubjectRef owner,
        ManagedCodexCredentialCleanup cleanup,
        OutcomeDeadline outcomeDeadline)
    {
        using var recording = outcomeDeadline.BeginRecording();
        try
        {
            var admission = await _commandPort.QueueCleanupAsync(
                owner,
                cleanup,
                recording.Token).ConfigureAwait(false);
            if (admission.Accepted)
                return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Managed Codex cleanup outcome dispatch failed for API key {ApiKeyId}.",
                cleanup.ApiKeyId);
            return false;
        }

        _logger.LogError(
            "Managed Codex cleanup outcome could not be recorded for API key {ApiKeyId}.",
            cleanup.ApiKeyId);
        return false;
    }

    private async Task<bool> TryDeleteNyxIdKeyAsync(
        string bearerToken,
        string apiKeyId,
        CancellationToken ct)
    {
        try
        {
            return await _nyxIdPort.RevokeApiKeyAsync(bearerToken, apiKeyId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryRevokeVaultAsync(
        string secretRef,
        string ownerScopeKey,
        string auditReason,
        CancellationToken ct)
    {
        try
        {
            var result = await _secretVault.RevokeAsync(
                new RevokeSecretRequest(
                    secretRef,
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    ownerScopeKey,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    auditReason),
                ct).ConfigureAwait(false);
            return result.Revoked;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
            throw Failure("managed_target_disabled", "Managed Codex execution is disabled.");
    }

    private static ManagedCodexCredentialDescriptor BuildDescriptor(
        ExternalSubjectRef owner,
        string apiKeyId,
        SecretReference reference,
        string chronoSandboxUserServiceId,
        string chronoLlmUserServiceId,
        DateTimeOffset expiresAt) =>
        new()
        {
            Owner = owner.Clone(),
            ApiKeyId = apiKeyId,
            SecretReference = reference.Clone(),
            ChronoSandboxUserServiceId = chronoSandboxUserServiceId,
            ChronoLlmUserServiceId = chronoLlmUserServiceId,
            ChronoSandboxServiceSlug = ManagedCodexOptions.ChronoSandboxServiceSlug,
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt.ToUniversalTime()),
            Status = ManagedCodexCredentialStatus.Active,
        };

    private static SecretReference ValidateStoredReference(
        SecretReference? reference,
        string expectedRef,
        string ownerScopeKey,
        DateTimeOffset expiresAt)
    {
        if (reference is null ||
            !string.Equals(reference.Ref, expectedRef, StringComparison.Ordinal) ||
            !string.Equals(reference.Purpose, CredentialSecretPurposes.ManagedCodexInvocationAgentKey, StringComparison.Ordinal) ||
            !string.Equals(reference.OwnerScopeKey, ownerScopeKey, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(reference.Fingerprint) ||
            reference.Version <= 0 ||
            reference.ExpiresAtUnixMs != expiresAt.ToUnixTimeMilliseconds())
        {
            throw Failure("managed_credential_vault_reference_invalid", "The secret vault returned an invalid managed Codex reference.");
        }
        return reference.Clone();
    }

    private static void ValidateActiveDescriptor(
        ManagedCodexCredentialDescriptor credential,
        ExternalSubjectRef owner,
        DateTimeOffset now,
        bool requireFutureExpiry = true)
    {
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        if (credential.Owner is null ||
            !string.Equals(ManagedCodexCredentialActorIdentity.From(credential.Owner), actorId, StringComparison.Ordinal) ||
            credential.SecretReference is null ||
            !string.Equals(credential.SecretReference.Purpose, CredentialSecretPurposes.ManagedCodexInvocationAgentKey, StringComparison.Ordinal) ||
            !string.Equals(credential.SecretReference.OwnerScopeKey, actorId, StringComparison.Ordinal) ||
            credential.SecretReference.Version <= 0 ||
            string.IsNullOrWhiteSpace(credential.SecretReference.Fingerprint) ||
            string.IsNullOrWhiteSpace(credential.ApiKeyId) ||
            credential.Status != ManagedCodexCredentialStatus.Active ||
            credential.ExpiresAt is null ||
            requireFutureExpiry && credential.ExpiresAt.ToDateTimeOffset() <= now)
        {
            throw Failure("managed_credential_invalid", "The projected managed Codex credential is invalid.");
        }
    }

    private static void ValidateIssuedKey(
        ManagedCodexNyxIdApiKey key,
        ManagedCodexNyxIdEligibility eligibility)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(key.Id) ||
            !string.Equals(key.Name, CredentialName, StringComparison.Ordinal) ||
            !HasExactApiKeyPolicy(key, eligibility))
        {
            throw Failure("managed_api_key_issue_invalid", "NyxID returned an invalid or over-broad managed Codex key.");
        }
    }

    private static bool HasExactApiKeyPolicy(
        ManagedCodexNyxIdApiKey key,
        ManagedCodexNyxIdEligibility eligibility) =>
        string.Equals(key.Scopes, "proxy", StringComparison.Ordinal) &&
        string.Equals(key.Platform, "codex", StringComparison.Ordinal) &&
        !key.AllowAllServices &&
        HasExactServiceIds(
            key.AllowedServiceIds,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId) &&
        !key.AllowAllNodes &&
        key.AllowedNodeIds is { Count: 0 };

    private static bool HasExactServiceIds(
        IReadOnlyList<string> actual,
        string sandboxId,
        string llmId)
    {
        var expected = new HashSet<string>(
            [sandboxId, llmId],
            StringComparer.Ordinal);
        return expected.Count == 2 &&
               actual is not null &&
               actual.Count == expected.Count &&
               actual.All(expected.Contains) &&
               actual.Distinct(StringComparer.Ordinal).Count() == expected.Count;
    }

    private static ManagedCodexCredentialMutationResult Result(
        string status,
        string actorId,
        string apiKeyId,
        DateTimeOffset expiresAt,
        DispatchAdmission admission) =>
        new(status, actorId, apiKeyId, expiresAt.ToUnixTimeMilliseconds(), admission.CommandId);

    private static ManagedCodexCredentialLifecycleException Failure(
        string code,
        string message) =>
        new(code, message);

}
