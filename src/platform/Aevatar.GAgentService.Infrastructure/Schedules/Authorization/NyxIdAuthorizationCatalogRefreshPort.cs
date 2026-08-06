using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.LlmCatalog;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;
using System.Runtime.ExceptionServices;

namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

public sealed class NyxIdAuthorizationCatalogRefreshPort : INyxIdAuthorizationCatalogRefreshPort
{
    public static readonly TimeSpan CatalogFreshnessLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CatalogObservationTimeout = TimeSpan.FromSeconds(15);

    private const string OrganizationOwnerNotSupportedFailureCode =
        "nyxid_catalog_organization_owner_not_supported";

    private readonly INyxIdAuthorizationCatalogCommandPort _commandPort;
    private readonly INyxIdAuthorizationCatalogQueryPort _catalogQueryPort;
    private readonly NyxIdAuthorizationCatalogRefreshPipeline _pipeline;
    private readonly TimeProvider _timeProvider;

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
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _pipeline = new NyxIdAuthorizationCatalogRefreshPipeline(
            _commandPort,
            nyxClientFactory,
            observationPreparation,
            observationProjection,
            _timeProvider,
            logger);
    }

    public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshPersonalAsync(
        string verifiedOwnerSubject,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedOwnerSubject);
        return RefreshAsync(PersonalOwner(verifiedOwnerSubject), bearerToken, ct);
    }

    public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
        AuthorizationOwnerIdentity owner,
        string bearerToken,
        CancellationToken ct = default) =>
        RefreshAsync(owner, bearerToken, requiredServiceIds: null, llmTarget: null, ct);

    public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
        AuthorizationOwnerIdentity owner,
        string bearerToken,
        NyxIdAuthorizationCatalogRefreshRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requiredServiceIds = NormalizeRequiredServiceIds(request.RequiredServices);
        var llmTarget = NormalizeLLMTarget(request.LLMTarget);
        if (llmTarget?.RouteKind == LLMRouteKind.NyxIdUserService)
        {
            requiredServiceIds = new SortedSet<string>(
                [llmTarget.NyxIdUserServiceId],
                StringComparer.Ordinal);
        }

        return requiredServiceIds.Count == 0 && llmTarget == null
            ? Task.FromResult(new NyxIdAuthorizationCatalogRefreshResult(
                NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable,
                "nyxid_exact_service_identity_unavailable"))
            : RefreshAsync(owner, bearerToken, requiredServiceIds, llmTarget, ct);
    }

    private async Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
        AuthorizationOwnerIdentity owner,
        string bearerToken,
        IReadOnlySet<string>? requiredServiceIds,
        ScheduledInvocationLLMRefreshRequirement? llmTarget,
        CancellationToken ct)
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
        var catalog = await _catalogQueryPort.GetAsync(normalizedOwner, ct).ConfigureAwait(false);
        var expectedLifecycleFence = catalog?.LifecycleFence ?? 0;
        return await _pipeline.RefreshAsync(
                normalizedOwner,
                bearerToken,
                refreshId,
                startedAt,
                requiredServiceIds,
                llmTarget,
                (refreshId, startedAt, dispatchCancellation) =>
                    _commandPort.BeginRefreshAsync(
                        normalizedOwner,
                        refreshId,
                        startedAt,
                        expectedLifecycleFence,
                        dispatchCancellation),
                ct)
            .ConfigureAwait(false);
    }

    private static IReadOnlySet<string> NormalizeRequiredServiceIds(
        IReadOnlyList<NyxIdUserServiceCapabilityRef>? requiredServices) =>
        new SortedSet<string>(
            (requiredServices ?? [])
                .Select(static service => service.UserServiceId?.Trim() ?? string.Empty)
                .Where(static serviceId => !string.IsNullOrWhiteSpace(serviceId)),
            StringComparer.Ordinal);

    private static ScheduledInvocationLLMRefreshRequirement? NormalizeLLMTarget(
        ScheduledInvocationLLMRefreshRequirement? target)
    {
        if (target == null)
            return null;
        if (target.UserConfigStateVersion <= 0)
            throw new InvalidOperationException("LLM refresh requires a positive UserConfig state version.");

        var serviceId = target.NyxIdUserServiceId?.Trim() ?? string.Empty;
        var serviceSlug = target.ServiceSlugSnapshot?.Trim() ?? string.Empty;
        var explicitModelId = target.ExplicitModelId ?? string.Empty;
        var selection = new LLMSelection
        {
            RouteKind = target.RouteKind,
            RouteValue = target.RouteKind switch
            {
                LLMRouteKind.Gateway => LLMSelectionPolicy.GatewayRoute,
                LLMRouteKind.NyxIdUserService => $"{ScheduledInvocationOwnerLLMSelectionPolicy.NyxIdProxyRoutePrefix}{serviceSlug}",
                _ => string.Empty,
            },
            NyxIdUserServiceId = serviceId,
            ServiceSlugSnapshot = serviceSlug,
            ModelSelection = new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.ExplicitModel,
                ModelId = explicitModelId,
            },
        };
        LLMSelectionPolicy.ValidateSelection(selection);
        return target with
        {
            RouteValue = selection.RouteValue,
            NyxIdUserServiceId = serviceId,
            ServiceSlugSnapshot = serviceSlug,
        };
    }

    private static AuthorizationOwnerIdentity PersonalOwner(string verifiedOwnerSubject) => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = verifiedOwnerSubject.Trim(),
    };
}

internal sealed class NyxIdAuthorizationCatalogRefreshPipeline
{
    private static readonly TimeSpan CatalogFreshnessLifetime =
        NyxIdAuthorizationCatalogRefreshPort.CatalogFreshnessLifetime;
    private static readonly TimeSpan CatalogObservationTimeout =
        NyxIdAuthorizationCatalogRefreshPort.CatalogObservationTimeout;

    private const string CatalogMismatchFailureCode = "nyxid_scope_plan_catalog_mismatch";
    private const string ProviderTimedOutFailureCode = "nyxid_catalog_refresh_provider_timed_out";
    private const string LLMModelsTransportFailureCode = "nyxid_llm_models_transport_failure";
    private const string LLMTargetInventoryMismatchFailureCode = "nyxid_llm_target_inventory_mismatch";
    private const string LLMModelsContractVersion = "openai-models/v1";
    private const string LLMModelsPolicyVersion = "nyxid-exact-route-models/v1";
    private const long MaxLLMModelsResponseBytes = 1024 * 1024;

    private readonly INyxIdAuthorizationCatalogCommandPort _commandPort;
    private readonly INyxIdApiClientFactory _nyxClientFactory;
    private readonly INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort
        _observationPreparation;
    private readonly INyxIdAuthorizationCatalogRefreshObservationProjectionPort
        _observationProjection;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NyxIdAuthorizationCatalogRefreshPort> _logger;

    public NyxIdAuthorizationCatalogRefreshPipeline(
        INyxIdAuthorizationCatalogCommandPort commandPort,
        INyxIdApiClientFactory nyxClientFactory,
        INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort observationPreparation,
        INyxIdAuthorizationCatalogRefreshObservationProjectionPort observationProjection,
        TimeProvider timeProvider,
        ILogger<NyxIdAuthorizationCatalogRefreshPort> logger)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _nyxClientFactory = nyxClientFactory ?? throw new ArgumentNullException(nameof(nyxClientFactory));
        _observationPreparation = observationPreparation ??
                                  throw new ArgumentNullException(nameof(observationPreparation));
        _observationProjection = observationProjection ??
                                 throw new ArgumentNullException(nameof(observationProjection));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
        AuthorizationOwnerIdentity normalizedOwner,
        string bearerToken,
        string refreshId,
        DateTimeOffset startedAt,
        IReadOnlySet<string>? requiredServiceIds,
        ScheduledInvocationLLMRefreshRequirement? llmTarget,
        Func<string, DateTimeOffset, CancellationToken, Task> beginRefresh,
        CancellationToken ct)
    {
        var actorId = NyxIdAuthorizationCatalogActorIds.Build(normalizedOwner);
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

            await beginRefresh(refreshId, startedAt, ct)
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
                        requiredServiceIds,
                        llmTarget,
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
        IReadOnlySet<string>? requiredServiceIds,
        ScheduledInvocationLLMRefreshRequirement? llmTarget,
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
                requiredServiceIds,
                llmTarget,
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
        IReadOnlySet<string>? requiredServiceIds,
        ScheduledInvocationLLMRefreshRequirement? llmTarget,
        CancellationToken ct)
    {
        var client = _nyxClientFactory.CreateClient();
        if (llmTarget?.RouteKind == LLMRouteKind.Gateway && requiredServiceIds?.Count == 0)
        {
            await RefreshGatewayTargetOnlyAsync(
                normalizedOwner,
                bearerToken,
                refreshId,
                llmTarget,
                client,
                requiredServiceIds,
                ct).ConfigureAwait(false);
            return;
        }

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
        if (llmTarget?.RouteKind == LLMRouteKind.NyxIdUserService &&
            !eligibleServices.Any(service =>
                string.Equals(service.Id, llmTarget.NyxIdUserServiceId, StringComparison.Ordinal) &&
                string.Equals(service.Slug, llmTarget.ServiceSlugSnapshot, StringComparison.Ordinal)))
        {
            await RecordCatalogUnstableRefreshAsync(
                normalizedOwner,
                refreshId,
                LLMTargetInventoryMismatchFailureCode,
                ct).ConfigureAwait(false);
            return;
        }

        if (requiredServiceIds is not null)
        {
            var missingRequiredServiceId = requiredServiceIds
                .FirstOrDefault(serviceId => !eligibleServices.Any(service => string.Equals(service.Id, serviceId, StringComparison.Ordinal)));
            if (missingRequiredServiceId != null)
            {
                await RecordCatalogUnstableRefreshAsync(
                    normalizedOwner,
                    refreshId,
                    $"nyxid_required_service_not_found:{missingRequiredServiceId}",
                    ct).ConfigureAwait(false);
                return;
            }

            eligibleServices = eligibleServices
                .Where(service => requiredServiceIds.Contains(service.Id))
                .ToArray();
        }
        var selectedServiceIds = eligibleServices.Select(static service => service.Id).ToArray();
        if (selectedServiceIds.Length == 0)
        {
            var emptyCatalogObservedAt = _timeProvider.GetUtcNow();
            await ObserveCatalogAsync(
                normalizedOwner,
                refreshId,
                emptyCatalogObservedAt,
                emptyCatalogObservedAt,
                NyxIdApiAccessResponseParser.ScopePlanContractVersion,
                NyxIdApiAccessResponseParser.ScopePlanPolicyVersion,
                [],
                requiredServiceIds,
                gatewayLLMTarget: null,
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
        var observedAt = _timeProvider.GetUtcNow();
        var freshUntil = observedAt.Add(CatalogFreshnessLifetime);
        var services = scopePlan.Services
            .Select(grant => MapServiceEvidence(
                inventoryById[grant.UserServiceId],
                grant,
                observedAt,
                freshUntil,
                scopePlan.EvaluatedAtUtc,
                scopePlan.ContractVersion,
                scopePlan.PolicyVersion))
            .ToArray();
        NyxIdAuthorizationLLMTargetEvidence? gatewayLLMTarget = null;
        if (llmTarget != null)
        {
            var targetResult = await ReadLLMTargetAsync(
                client,
                bearerToken,
                llmTarget,
                ct).ConfigureAwait(false);
            if (targetResult.FailureCode != null)
            {
                await RecordLLMProviderFailureAsync(
                    normalizedOwner,
                    refreshId,
                    targetResult.FailureCode,
                    ct).ConfigureAwait(false);
                return;
            }

            var targetEvidence = BuildLLMTargetEvidence(
                llmTarget,
                targetResult.ModelCatalog!,
                observedAt);
            if (llmTarget.RouteKind == LLMRouteKind.Gateway)
            {
                gatewayLLMTarget = targetEvidence;
            }
            else
            {
                var service = services.Single(service => string.Equals(
                    service.UserServiceId,
                    llmTarget.NyxIdUserServiceId,
                    StringComparison.Ordinal));
                service.LlmTarget = targetEvidence;
            }
        }

        await ObserveCatalogAsync(
            normalizedOwner,
            refreshId,
            observedAt,
            scopePlan.EvaluatedAtUtc,
            scopePlan.ContractVersion,
            scopePlan.PolicyVersion,
            services,
            requiredServiceIds,
            gatewayLLMTarget,
            ct).ConfigureAwait(false);
    }

    private async Task RefreshGatewayTargetOnlyAsync(
        AuthorizationOwnerIdentity owner,
        string bearerToken,
        string refreshId,
        ScheduledInvocationLLMRefreshRequirement target,
        NyxIdApiClient client,
        IReadOnlySet<string> requiredServiceIds,
        CancellationToken ct)
    {
        var targetResult = await ReadLLMTargetAsync(client, bearerToken, target, ct).ConfigureAwait(false);
        if (targetResult.FailureCode != null)
        {
            await RecordLLMProviderFailureAsync(owner, refreshId, targetResult.FailureCode, ct)
                .ConfigureAwait(false);
            return;
        }

        var observedAt = _timeProvider.GetUtcNow();
        var evidence = BuildLLMTargetEvidence(target, targetResult.ModelCatalog!, observedAt);
        await ObserveCatalogAsync(
            owner,
            refreshId,
            observedAt,
            observedAt,
            LLMModelsContractVersion,
            LLMModelsPolicyVersion,
            [],
            requiredServiceIds,
            evidence,
            ct).ConfigureAwait(false);
    }

    private async Task<LLMTargetReadResult> ReadLLMTargetAsync(
        NyxIdApiClient client,
        string bearerToken,
        ScheduledInvocationLLMRefreshRequirement target,
        CancellationToken ct)
    {
        NyxIdProxyTextResponse response;
        try
        {
            response = await client.GetLlmRouteModelsBoundedAsync(
                bearerToken,
                target.RouteKind,
                target.RouteKind == LLMRouteKind.NyxIdUserService
                    ? target.NyxIdUserServiceId
                    : null,
                target.RouteKind == LLMRouteKind.NyxIdUserService
                    ? target.ServiceSlugSnapshot
                    : null,
                MaxLLMModelsResponseBytes,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return LLMTargetReadResult.Failed(ProviderTimedOutFailureCode);
        }

        if (response.Succeeded)
        {
            return LLMTargetReadResult.Observed(
                NyxIdLlmServiceCatalogParser.ParseOpenAIModelsResponse(response.Content));
        }

        if (response.Detail is "content_length_exceeds_max_bytes" or "content_exceeds_max_bytes")
        {
            return LLMTargetReadResult.Observed(new LLMModelCatalog
            {
                Certainty = LLMModelCatalogCertainty.NotVerifiable,
                DiagnosticKind = LLMModelCatalogDiagnosticKind.ResponseTooLarge,
            });
        }

        if (response.HttpStatus is 401 or 403)
        {
            return LLMTargetReadResult.Observed(new LLMModelCatalog
            {
                Certainty = LLMModelCatalogCertainty.Unavailable,
                DiagnosticKind = LLMModelCatalogDiagnosticKind.AccessDenied,
            });
        }

        if (response.HttpStatus is > 0 and < 500 && response.HttpStatus != 429)
        {
            return LLMTargetReadResult.Observed(new LLMModelCatalog
            {
                Certainty = LLMModelCatalogCertainty.Unavailable,
                DiagnosticKind = LLMModelCatalogDiagnosticKind.RouteNotReady,
            });
        }

        return LLMTargetReadResult.Failed(LLMModelsTransportFailureCode);
    }

    private async Task RecordLLMProviderFailureAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        string failureCode,
        CancellationToken ct)
    {
        await _commandPort.RecordRefreshFailureAsync(
            owner,
            refreshId,
            _timeProvider.GetUtcNow(),
            failureCode,
            ct: ct).ConfigureAwait(false);
        LogWithoutThrowing(
            LogLevel.Warning,
            "NyxID LLM catalog target refresh failed. ownerKind={OwnerKind} failureCode={FailureCode}",
            owner.OwnerKind,
            failureCode);
    }

    private static NyxIdAuthorizationLLMTargetEvidence BuildLLMTargetEvidence(
        ScheduledInvocationLLMRefreshRequirement target,
        LLMModelCatalog modelCatalog,
        DateTimeOffset observedAt) => new()
    {
        RouteKind = target.RouteKind,
        RouteValue = target.RouteKind == LLMRouteKind.Gateway
            ? LLMSelectionPolicy.GatewayRoute
            : $"{ScheduledInvocationOwnerLLMSelectionPolicy.NyxIdProxyRoutePrefix}{target.ServiceSlugSnapshot}",
        NyxIdUserServiceId = target.RouteKind == LLMRouteKind.NyxIdUserService
            ? target.NyxIdUserServiceId
            : string.Empty,
        ServiceSlugSnapshot = target.RouteKind == LLMRouteKind.NyxIdUserService
            ? target.ServiceSlugSnapshot
            : string.Empty,
        ModelCatalog = modelCatalog.Clone(),
        ObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(observedAt),
        FreshUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
            observedAt.Add(CatalogFreshnessLifetime)),
        EvaluatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(observedAt),
        AuthorityContractVersion = LLMModelsContractVersion,
        AuthorityPolicyVersion = LLMModelsPolicyVersion,
    };

    private async Task ObserveCatalogAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        DateTimeOffset observedAt,
        DateTimeOffset evaluatedAt,
        string contractVersion,
        string policyVersion,
        IReadOnlyList<NyxIdAuthorizationServiceEvidence> services,
        IReadOnlySet<string>? requiredServiceIds,
        NyxIdAuthorizationLLMTargetEvidence? gatewayLLMTarget,
        CancellationToken ct)
    {
        var coverage = requiredServiceIds is null
            ? NyxIdAuthorizationCatalogObservationCoverage.FullOwner
            : NyxIdAuthorizationCatalogObservationCoverage.RequiredServiceSubset;
        var contentDigest = coverage == NyxIdAuthorizationCatalogObservationCoverage.FullOwner
            ? NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(owner, services, gatewayLLMTarget)
            : string.Empty;
        await _commandPort.ObserveAsync(new NyxIdAuthorizationCatalogObservation(
            owner,
            refreshId,
            observedAt,
            observedAt.Add(CatalogFreshnessLifetime),
            contractVersion,
            policyVersion,
            evaluatedAt,
            contentDigest,
            services,
            coverage,
            requiredServiceIds?.ToArray(),
            gatewayLLMTarget), ct).ConfigureAwait(false);
    }

    private sealed record LLMTargetReadResult(
        LLMModelCatalog? ModelCatalog,
        string? FailureCode)
    {
        public static LLMTargetReadResult Observed(LLMModelCatalog catalog) => new(catalog, null);

        public static LLMTargetReadResult Failed(string failureCode) => new(null, failureCode);
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
            ct: ct);
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
            await _commandPort.RecordRefreshFailureAsync(owner, refreshId, now, code, ct: ct);
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
        LogCatalogUnstable(owner, code);
    }

    private async Task RecordCatalogUnstableRefreshAsync(
        AuthorizationOwnerIdentity owner,
        string refreshId,
        string code,
        CancellationToken ct)
    {
        await _commandPort.RecordRefreshFailureAsync(
            owner,
            refreshId,
            _timeProvider.GetUtcNow(),
            code,
            NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable,
            ct).ConfigureAwait(false);
        LogCatalogUnstable(owner, code);
    }

    private void LogCatalogUnstable(AuthorizationOwnerIdentity owner, string code) =>
        LogWithoutThrowing(
            LogLevel.Warning,
            "NyxID authorization catalog response was unstable. ownerKind={OwnerKind} failureCode={FailureCode}",
            owner.OwnerKind,
            code);

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
        NyxIdScopePlanServiceGrant grant,
        DateTimeOffset observedAt,
        DateTimeOffset freshUntil,
        DateTimeOffset evaluatedAt,
        string contractVersion,
        string policyVersion)
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
            ObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(observedAt),
            FreshUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(freshUntil),
            EvaluatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(evaluatedAt),
            AuthorityContractVersion = contractVersion,
            AuthorityPolicyVersion = policyVersion,
        };
        service.NodeIds.Add(grant.NodeGrant.NodeIds);
        return service;
    }

    private static string ResolveDisplayName(NyxIdUserService service) =>
        Normalize(service.Label) ?? Normalize(service.CatalogServiceName) ?? service.Slug;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
