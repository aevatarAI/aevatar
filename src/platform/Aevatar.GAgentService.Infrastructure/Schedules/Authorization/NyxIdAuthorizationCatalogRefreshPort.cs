using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Microsoft.Extensions.Logging;
using System.Runtime.ExceptionServices;

namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

public sealed class NyxIdAuthorizationCatalogRefreshPort : INyxIdAuthorizationCatalogRefreshPort
{
    public static readonly TimeSpan CatalogFreshnessLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CatalogObservationTimeout = TimeSpan.FromSeconds(15);

    private const string OrganizationOwnerNotSupportedFailureCode =
        "nyxid_catalog_organization_owner_not_supported";
    private const string CatalogMismatchFailureCode = "nyxid_scope_plan_catalog_mismatch";
    private const string ProviderTimedOutFailureCode = "nyxid_catalog_refresh_provider_timed_out";

    private readonly INyxIdAuthorizationCatalogCommandPort _commandPort;
    private readonly INyxIdAuthorizationCatalogQueryPort _catalogQueryPort;
    private readonly INyxIdApiClientFactory _nyxClientFactory;
    private readonly INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort
        _observationPreparation;
    private readonly INyxIdAuthorizationCatalogRefreshObservationProjectionPort
        _observationProjection;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdAuthorizationCatalogRefreshPort> _logger;

    public NyxIdAuthorizationCatalogRefreshPort(
        INyxIdAuthorizationCatalogCommandPort commandPort,
        INyxIdAuthorizationCatalogQueryPort catalogQueryPort,
        INyxIdApiClientFactory nyxClientFactory,
        INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort observationPreparation,
        INyxIdAuthorizationCatalogRefreshObservationProjectionPort observationProjection,
        TimeProvider timeProvider,
        ILogger<NyxIdAuthorizationCatalogRefreshPort> logger)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _catalogQueryPort = catalogQueryPort ?? throw new ArgumentNullException(nameof(catalogQueryPort));
        _nyxClientFactory = nyxClientFactory ?? throw new ArgumentNullException(nameof(nyxClientFactory));
        _observationPreparation = observationPreparation ??
                                  throw new ArgumentNullException(nameof(observationPreparation));
        _observationProjection = observationProjection ??
                                 throw new ArgumentNullException(nameof(observationProjection));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshPersonalAsync(
        string verifiedOwnerSubject,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedOwnerSubject);
        return RefreshAsync(new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = verifiedOwnerSubject.Trim(),
        }, bearerToken, ct);
    }

    public async Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
        AuthorizationOwnerIdentity owner,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        if (!string.Equals(
                owner.Authority?.Trim(),
                NyxIdAuthorizationAuthorities.NyxId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("NyxID authorization catalog owner authority is not supported.");
        }
        if (owner.OwnerKind != AuthorizationOwnerKind.Personal)
        {
            return new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.OwnerNotSupported,
                OrganizationOwnerNotSupportedFailureCode);
        }
        if (string.IsNullOrWhiteSpace(owner.OwnerSubject))
            throw new InvalidOperationException("NyxID authorization catalog owner subject is required.");

        var normalizedOwner = owner.Clone();
        normalizedOwner.Authority = NyxIdAuthorizationAuthorities.NyxId;
        normalizedOwner.OwnerSubject = owner.OwnerSubject.Trim();
        var refreshId = Guid.NewGuid().ToString("N");
        var startedAt = _timeProvider.GetUtcNow();
        var actorId = NyxIdAuthorizationCatalogActorIds.Build(normalizedOwner);
        var catalog = await _catalogQueryPort.GetAsync(normalizedOwner, ct).ConfigureAwait(false);
        var expectedLifecycleFence = catalog?.LifecycleFence ?? 0;
        NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation? preparation = null;
        EventSinkProjectionAttachment<INyxIdAuthorizationCatalogRefreshObservationProjectionLease>?
            attachment = null;
        await using var sink = new EventChannel<NyxIdAuthorizationCatalogRefreshCommittedOutcome>(8);
        NyxIdAuthorizationCatalogRefreshResult? result = null;
        ExceptionDispatchInfo? operationFailure = null;
        try
        {
            preparation = await _observationPreparation.PrepareAsync(actorId, refreshId, ct)
                .ConfigureAwait(false);
            if (preparation == null)
                throw new InvalidOperationException("nyxid_catalog_refresh_observation_unavailable");

            attachment = await _observationProjection.AttachExistingRefreshProjectionAsync(
                    actorId,
                    refreshId,
                    sink,
                    ct)
                .ConfigureAwait(false);
            if (attachment == null)
                throw new InvalidOperationException("nyxid_catalog_refresh_observation_unavailable");

            await _commandPort.BeginRefreshAsync(
                    normalizedOwner,
                    refreshId,
                    startedAt,
                    expectedLifecycleFence,
                    ct)
                .ConfigureAwait(false);

            var began = await AwaitOutcomeAsync(
                    sink,
                    refreshId,
                    static outcome => outcome.Status is
                        NyxIdAuthorizationCatalogRefreshOutcomeStatus.Started or
                        NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded,
                    ct)
                .ConfigureAwait(false);
            if (began == null)
            {
                result = ObservationTimedOut();
            }
            else if (began.Status == NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded)
            {
                result = ToRefreshResult(began);
            }
            else
            {
                result = await RunProviderWhileObservingAsync(
                        normalizedOwner,
                        bearerToken,
                        refreshId,
                        sink,
                        ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            operationFailure = ExceptionDispatchInfo.Capture(ex);
        }

        var cleanupFailure = await ReleaseObservationAsync(attachment, preparation).ConfigureAwait(false);
        operationFailure?.Throw();
        if (cleanupFailure != null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();

        return result ?? throw new InvalidOperationException("nyxid_catalog_refresh_result_missing");
    }

    private async Task<NyxIdAuthorizationCatalogRefreshResult> RunProviderWhileObservingAsync(
        AuthorizationOwnerIdentity normalizedOwner,
        string bearerToken,
        string refreshId,
        EventChannel<NyxIdAuthorizationCatalogRefreshCommittedOutcome> sink,
        CancellationToken ct)
    {
        var providerCancellation = new CancellationTokenSource();
        var providerCancellationTransferred = false;
        try
        {
            using var terminalCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var terminalTask = AwaitTerminalResultAsync(sink, refreshId, terminalCancellation.Token);
            var providerTask = RefreshProviderAsync(
                normalizedOwner,
                bearerToken,
                refreshId,
                providerCancellation.Token);
            var completed = await Task.WhenAny(terminalTask, providerTask).ConfigureAwait(false);
            if (ReferenceEquals(completed, terminalTask))
            {
                NyxIdAuthorizationCatalogRefreshResult terminal;
                try
                {
                    terminal = await terminalTask.ConfigureAwait(false);
                }
                catch
                {
                    var observation = ObserveLosingProviderTask(providerTask, providerCancellation);
                    providerCancellationTransferred = true;
                    CancelProviderWithoutThrowing(observation);
                    throw;
                }

                if (terminal.Status == NyxIdAuthorizationCatalogRefreshStatus.Superseded)
                {
                    var observation = ObserveLosingProviderTask(providerTask, providerCancellation);
                    providerCancellationTransferred = true;
                    CancelProviderWithoutThrowing(observation);
                    return terminal;
                }

                await providerTask.ConfigureAwait(false);
                return terminal;
            }

            try
            {
                await providerTask.ConfigureAwait(false);
            }
            catch
            {
                terminalCancellation.Cancel();
                await ObserveAfterCancellationAsync(terminalTask).ConfigureAwait(false);
                throw;
            }

            using var timeout = new CancellationTokenSource(CatalogObservationTimeout, _timeProvider);
            using var observationDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var deadlineTask = Task.Delay(Timeout.InfiniteTimeSpan, _timeProvider, observationDeadline.Token);
            completed = await Task.WhenAny(terminalTask, deadlineTask).ConfigureAwait(false);
            if (ReferenceEquals(completed, terminalTask))
                return await terminalTask.ConfigureAwait(false);

            terminalCancellation.Cancel();
            await ObserveAfterCancellationAsync(terminalTask).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                ct.ThrowIfCancellationRequested();
            return ObservationTimedOut();
        }
        finally
        {
            if (!providerCancellationTransferred)
                providerCancellation.Dispose();
        }
    }

    private async Task<Exception?> ReleaseObservationAsync(
        EventSinkProjectionAttachment<INyxIdAuthorizationCatalogRefreshObservationProjectionLease>? attachment,
        NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation? preparation)
    {
        Exception? firstFailure = null;

        async Task ReleaseAsync(string stage, Func<Task> release)
        {
            try
            {
                await release().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
                LogWithoutThrowing(
                    LogLevel.Warning,
                    "Failed to release NyxID catalog refresh observation resource at stage {CleanupStage}.",
                    stage);
            }
        }

        if (attachment != null)
        {
            await ReleaseAsync(
                "live_sink",
                () => _observationProjection.DetachLiveSinkAsync(
                    attachment.LiveSinkLease,
                    CancellationToken.None)).ConfigureAwait(false);
            await ReleaseAsync(
                "actor_projection",
                () => _observationProjection.ReleaseActorProjectionAsync(
                    attachment.ProjectionLease,
                    CancellationToken.None)).ConfigureAwait(false);
        }

        if (preparation != null)
        {
            await ReleaseAsync(
                "scope_preparation",
                () => _observationPreparation.ReleaseAsync(
                    preparation,
                    CancellationToken.None)).ConfigureAwait(false);
        }

        return firstFailure;
    }

    private async Task RefreshProviderAsync(
        AuthorizationOwnerIdentity normalizedOwner,
        string bearerToken,
        string refreshId,
        CancellationToken ct)
    {
        var client = _nyxClientFactory.CreateClient();
        string inventoryResponse;
        try
        {
            inventoryResponse = await client.ListUserServicesAsync(bearerToken, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await RecordProviderTimeoutAsync(normalizedOwner, refreshId, ct).ConfigureAwait(false);
            return;
        }
        var inventoryResult = NyxIdApiAccessResponseParser.ParseUserServices(inventoryResponse);
        if (!inventoryResult.Succeeded)
        {
            await HandleFailureAsync(
                normalizedOwner,
                refreshId,
                inventoryResult.Failure,
                ct).ConfigureAwait(false);
            return;
        }

        var eligibleServices = inventoryResult.Value!.Services
            .Where(IsEligible)
            .OrderBy(static service => service.Id, StringComparer.Ordinal)
            .ToArray();
        var selectedServiceIds = eligibleServices.Select(static service => service.Id).ToArray();
        if (selectedServiceIds.Length == 0)
        {
            var observedAt = _timeProvider.GetUtcNow();
            await ObserveCatalogAsync(
                normalizedOwner,
                refreshId,
                observedAt,
                observedAt,
                NyxIdApiAccessResponseParser.ScopePlanContractVersion,
                NyxIdApiAccessResponseParser.ScopePlanPolicyVersion,
                [],
                ct).ConfigureAwait(false);
            return;
        }

        string scopePlanResponse;
        try
        {
            scopePlanResponse = await client.PlanApiKeyScopeAsync(
                bearerToken,
                selectedServiceIds,
                targetOrganizationId: null,
                ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await RecordProviderTimeoutAsync(normalizedOwner, refreshId, ct).ConfigureAwait(false);
            return;
        }
        var scopePlanResult = NyxIdApiAccessResponseParser.ParseScopePlan(scopePlanResponse);
        if (!scopePlanResult.Succeeded)
        {
            await HandleFailureAsync(
                normalizedOwner,
                refreshId,
                scopePlanResult.Failure,
                ct).ConfigureAwait(false);
            return;
        }

        var scopePlan = scopePlanResult.Value!;
        if (!MatchesPersonalCatalog(scopePlan, normalizedOwner, eligibleServices))
        {
            await InvalidateUnstableAsync(
                normalizedOwner,
                refreshId,
                CatalogMismatchFailureCode,
                ct).ConfigureAwait(false);
            return;
        }

        var inventoryById = eligibleServices.ToDictionary(static service => service.Id, StringComparer.Ordinal);
        var services = scopePlan.Services
            .Select(grant => MapServiceEvidence(inventoryById[grant.UserServiceId], grant))
            .ToArray();
        await ObserveCatalogAsync(
            normalizedOwner,
            refreshId,
            _timeProvider.GetUtcNow(),
            scopePlan.EvaluatedAtUtc,
            scopePlan.ContractVersion,
            scopePlan.PolicyVersion,
            services,
            ct).ConfigureAwait(false);

    }

    private async Task ObserveCatalogAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset observedAt,
        DateTimeOffset evaluatedAt,
        string contractVersion,
        string policyVersion,
        IReadOnlyList<NyxIdAuthorizationServiceEvidence> services,
        CancellationToken ct)
    {
        var contentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(owner, services);
        await _commandPort.ObserveAsync(new NyxIdAuthorizationCatalogObservation(
            owner,
            refreshId,
            observedAt,
            observedAt.Add(CatalogFreshnessLifetime),
            contractVersion,
            policyVersion,
            evaluatedAt,
            contentDigest,
            services), ct).ConfigureAwait(false);
    }

    private async Task RecordProviderTimeoutAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        CancellationToken ct)
    {
        await _commandPort.RecordRefreshFailureAsync(
            owner,
            refreshId,
            _timeProvider.GetUtcNow(),
            ProviderTimedOutFailureCode,
            ct);
        LogWithoutThrowing(
            LogLevel.Warning,
            "NyxID authorization catalog provider request timed out. ownerKind={OwnerKind} failureCode={FailureCode}",
            owner.OwnerKind,
            ProviderTimedOutFailureCode);
    }

    private async Task HandleFailureAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        NyxIdApiAccessFailure? failure,
        CancellationToken ct)
    {
        var code = string.IsNullOrWhiteSpace(failure?.Code)
            ? "nyxid_catalog_refresh_failed"
            : failure.Code;
        var now = _timeProvider.GetUtcNow();
        if (failure?.Kind is NyxIdApiAccessFailureKind.Unauthorized or NyxIdApiAccessFailureKind.Forbidden)
        {
            await _commandPort.InvalidateRefreshAsync(
                owner,
                refreshId,
                now,
                code,
                NyxIdAuthorizationCatalogRefreshOutcomeStatus.AccessDenied,
                ct);
            LogWithoutThrowing(
                LogLevel.Warning,
                "NyxID authorization catalog access was denied. ownerKind={OwnerKind} failureCode={FailureCode}",
                owner.OwnerKind,
                code);
            return;
        }

        if (failure?.Kind is NyxIdApiAccessFailureKind.RateLimited or
            NyxIdApiAccessFailureKind.Transport or
            NyxIdApiAccessFailureKind.Transient)
        {
            await _commandPort.RecordRefreshFailureAsync(owner, refreshId, now, code, ct);
            LogWithoutThrowing(
                LogLevel.Warning,
                "NyxID authorization catalog refresh failed transiently. ownerKind={OwnerKind} failureCode={FailureCode}",
                owner.OwnerKind,
                code);
            return;
        }

        await InvalidateUnstableAsync(owner, refreshId, code, ct).ConfigureAwait(false);
    }

    private async Task InvalidateUnstableAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        string code,
        CancellationToken ct)
    {
        await _commandPort.InvalidateRefreshAsync(
            owner,
            refreshId,
            _timeProvider.GetUtcNow(),
            code,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.CatalogUnstable,
            ct);
        LogWithoutThrowing(
            LogLevel.Warning,
            "NyxID authorization catalog response was unstable. ownerKind={OwnerKind} failureCode={FailureCode}",
            owner.OwnerKind,
            code);
    }

    private async Task ObserveAfterCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogWithoutThrowing(
                LogLevel.Debug,
                "Canceled the losing NyxID catalog refresh task after terminal observation.");
        }
        catch (Exception)
        {
            LogWithoutThrowing(
                LogLevel.Warning,
                "The losing NyxID catalog refresh task failed after terminal observation.");
        }
    }

    private LosingProviderTaskObservation ObserveLosingProviderTask(
        Task providerTask,
        CancellationTokenSource providerCancellation)
    {
        var observation = new LosingProviderTaskObservation(providerCancellation, _logger);
        _ = providerTask.ContinueWith(
            static (completed, state) =>
            {
                var observation = (LosingProviderTaskObservation)state!;
                try
                {
                    if (completed.IsCanceled)
                    {
                        LogWithoutThrowing(
                            observation.Logger,
                            LogLevel.Debug,
                            "Canceled the losing NyxID catalog refresh provider task after terminal observation.");
                    }
                    else if (completed.IsFaulted)
                    {
                        _ = completed.Exception;
                        LogWithoutThrowing(
                            observation.Logger,
                            LogLevel.Warning,
                            "The losing NyxID catalog refresh provider task failed after terminal observation.");
                    }
                }
                catch
                {
                    _ = completed.Exception;
                }
                finally
                {
                    observation.MarkProviderCompletion();
                }
            },
            observation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return observation;
    }

    private void CancelProviderWithoutThrowing(LosingProviderTaskObservation observation)
    {
        try
        {
            observation.Cancellation.Cancel();
        }
        catch
        {
            LogWithoutThrowing(
                LogLevel.Warning,
                "A NyxID catalog refresh provider cancellation callback failed after terminal observation.");
        }
        finally
        {
            observation.MarkCancellationAttemptCompletion();
        }
    }

    private void LogWithoutThrowing(LogLevel logLevel, string message, params object?[] args) =>
        LogWithoutThrowing(_logger, logLevel, message, args);

    private static void LogWithoutThrowing(
        ILogger logger,
        LogLevel logLevel,
        string message,
        params object?[] args)
    {
        try
        {
            logger.Log(logLevel, message, args);
        }
        catch
        {
            // Logging must not replace the operation result or interrupt resource cleanup.
            return;
        }
    }

    private sealed class LosingProviderTaskObservation(
        CancellationTokenSource cancellation,
        ILogger<NyxIdAuthorizationCatalogRefreshPort> logger)
    {
        private int _providerCompletionMarked;
        private int _cancellationAttemptCompletionMarked;
        private int _remainingLifetimeOwners = 2;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public ILogger<NyxIdAuthorizationCatalogRefreshPort> Logger { get; } = logger;

        public void MarkProviderCompletion() =>
            ReleaseLifetimeOwner(ref _providerCompletionMarked);

        public void MarkCancellationAttemptCompletion() =>
            ReleaseLifetimeOwner(ref _cancellationAttemptCompletionMarked);

        private void ReleaseLifetimeOwner(ref int completionMarker)
        {
            if (Interlocked.Exchange(ref completionMarker, 1) != 0)
                return;

            if (Interlocked.Decrement(ref _remainingLifetimeOwners) == 0)
                Cancellation.Dispose();
        }
    }

    private async Task<NyxIdAuthorizationCatalogRefreshResult> AwaitTerminalResultAsync(
        EventChannel<NyxIdAuthorizationCatalogRefreshCommittedOutcome> sink,
        string refreshId,
        CancellationToken ct)
    {
        var outcome = await AwaitOutcomeCoreAsync(
                sink,
                refreshId,
                static candidate => candidate.Status !=
                                    NyxIdAuthorizationCatalogRefreshOutcomeStatus.Started,
                ct)
            .ConfigureAwait(false);
        return ToRefreshResult(outcome);
    }

    private async Task<NyxIdAuthorizationCatalogRefreshCommittedOutcome?> AwaitOutcomeAsync(
        EventChannel<NyxIdAuthorizationCatalogRefreshCommittedOutcome> sink,
        string refreshId,
        Func<NyxIdAuthorizationCatalogRefreshCommittedOutcome, bool> matchesStage,
        CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(CatalogObservationTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await AwaitOutcomeCoreAsync(sink, refreshId, matchesStage, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<NyxIdAuthorizationCatalogRefreshCommittedOutcome> AwaitOutcomeCoreAsync(
        EventChannel<NyxIdAuthorizationCatalogRefreshCommittedOutcome> sink,
        string refreshId,
        Func<NyxIdAuthorizationCatalogRefreshCommittedOutcome, bool> matchesStage,
        CancellationToken ct)
    {
        await foreach (var outcome in sink.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (string.Equals(outcome.RefreshId, refreshId, StringComparison.Ordinal) &&
                matchesStage(outcome))
            {
                return outcome;
            }
        }

        throw new InvalidOperationException("nyxid_catalog_refresh_observation_ended");
    }

    private static NyxIdAuthorizationCatalogRefreshResult ToRefreshResult(
        NyxIdAuthorizationCatalogRefreshCommittedOutcome outcome) => outcome.Status switch
    {
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed =>
            NyxIdAuthorizationCatalogRefreshResult.ObservedAt(outcome.StateVersion),
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.Failed =>
            new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.Failed,
                outcome.FailureCode,
                outcome.StateVersion),
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.AccessDenied =>
            new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.AccessDenied,
                outcome.FailureCode,
                outcome.StateVersion),
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.CatalogUnstable =>
            new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable,
                outcome.FailureCode,
                outcome.StateVersion),
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded =>
            new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.Superseded,
                outcome.FailureCode,
                outcome.StateVersion),
        _ => throw new InvalidOperationException("nyxid_catalog_refresh_observation_status_invalid"),
    };

    private static NyxIdAuthorizationCatalogRefreshResult ObservationTimedOut() =>
        new(
            NyxIdAuthorizationCatalogRefreshStatus.ObservationTimedOut,
            "nyxid_catalog_refresh_observation_timed_out");

    private static bool IsEligible(NyxIdUserService service) =>
        service.IsActive &&
        (service.CredentialSource.Kind == NyxIdUserServiceCredentialSourceKind.Personal ||
         service.CredentialSource.Kind == NyxIdUserServiceCredentialSourceKind.Organization &&
         service.CredentialSource.Allowed);

    private static bool MatchesPersonalCatalog(
        NyxIdApiKeyScopePlan scopePlan,
        AuthorizationOwnerIdentity owner,
        IReadOnlyList<NyxIdUserService> selectedServices)
    {
        var selectedServiceIds = selectedServices.Select(static service => service.Id);
        return string.Equals(
                   scopePlan.Authority,
                   NyxIdAuthorizationAuthorities.NyxId,
                   StringComparison.Ordinal) &&
               scopePlan.AuthenticatedActor == new NyxIdScopePlanPrincipal(
                   owner.OwnerSubject,
                   NyxIdScopePlanPrincipalKind.Personal) &&
               scopePlan.IntendedKeyOwner == new NyxIdScopePlanPrincipal(
                   owner.OwnerSubject,
                   NyxIdScopePlanPrincipalKind.Personal) &&
               scopePlan.AllowedServiceIds.SequenceEqual(selectedServiceIds, StringComparer.Ordinal) &&
               scopePlan.Services.Select(static service => service.UserServiceId)
                   .SequenceEqual(selectedServices.Select(static service => service.Id), StringComparer.Ordinal) &&
               scopePlan.Services.Zip(
                       selectedServices,
                       (grant, inventory) => MatchesResourceOwnerProvenance(grant, inventory, owner.OwnerSubject))
                   .All(static matches => matches);
    }

    private static bool MatchesResourceOwnerProvenance(
        NyxIdScopePlanServiceGrant grant,
        NyxIdUserService inventory,
        string authenticatedOwnerSubject) => inventory.CredentialSource.Kind switch
    {
        NyxIdUserServiceCredentialSourceKind.Personal =>
            grant.ResourceOwner == new NyxIdScopePlanPrincipal(
                authenticatedOwnerSubject,
                NyxIdScopePlanPrincipalKind.Personal),
        NyxIdUserServiceCredentialSourceKind.Organization =>
            !string.IsNullOrWhiteSpace(inventory.CredentialSource.OrganizationId) &&
            grant.ResourceOwner == new NyxIdScopePlanPrincipal(
                inventory.CredentialSource.OrganizationId,
                NyxIdScopePlanPrincipalKind.Organization),
        _ => false,
    };

    private static NyxIdAuthorizationServiceEvidence MapServiceEvidence(
        NyxIdUserService inventory,
        NyxIdScopePlanServiceGrant grant)
    {
        var service = new NyxIdAuthorizationServiceEvidence
        {
            UserServiceId = inventory.Id,
            ServiceSlug = inventory.Slug,
            DisplayName = ResolveDisplayName(inventory),
            Access = NyxIdAuthorizationAccess.Permitted,
            NodeGrantRequirement = grant.NodeGrant.Kind switch
            {
                NyxIdScopePlanNodeGrantKind.NotRequired => AuthorizationGrantRequirement.NotRequired,
                NyxIdScopePlanNodeGrantKind.Required => AuthorizationGrantRequirement.Required,
                _ => AuthorizationGrantRequirement.Unspecified,
            },
            ResourceOwner = new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = grant.ResourceOwner.Kind switch
                {
                    NyxIdScopePlanPrincipalKind.Personal => AuthorizationOwnerKind.Personal,
                    NyxIdScopePlanPrincipalKind.Organization => AuthorizationOwnerKind.Organization,
                    _ => AuthorizationOwnerKind.Unspecified,
                },
                OwnerSubject = grant.ResourceOwner.Id,
            },
        };
        service.NodeIds.Add(grant.NodeGrant.NodeIds);
        return service;
    }

    private static string ResolveDisplayName(NyxIdUserService service) =>
        Normalize(service.Label) ?? Normalize(service.CatalogServiceName) ?? service.Slug;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
