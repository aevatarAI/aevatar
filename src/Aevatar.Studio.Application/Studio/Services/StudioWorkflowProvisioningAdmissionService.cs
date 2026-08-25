using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

internal sealed class StudioWorkflowProvisioningAdmissionService
{
    private const string DurableAuthorizationUnavailableCode =
        "DURABLE_AUTHORIZATION_UNAVAILABLE";

    private readonly IWorkflowExternalCapabilityAdmissionService _admissionService;
    private readonly INyxIdAuthorizationCatalogRefreshPort? _catalogRefreshPort;
    private readonly INyxIdAuthorizationCatalogVisibilityPort? _catalogVisibilityPort;
    private readonly ILogger<StudioWorkflowProvisioningService> _logger;

    public StudioWorkflowProvisioningAdmissionService(
        IWorkflowExternalCapabilityAdmissionService admissionService,
        INyxIdAuthorizationCatalogRefreshPort? catalogRefreshPort,
        INyxIdAuthorizationCatalogVisibilityPort? catalogVisibilityPort,
        ILogger<StudioWorkflowProvisioningService>? logger = null)
    {
        _admissionService = admissionService ?? throw new ArgumentNullException(nameof(admissionService));
        _catalogRefreshPort = catalogRefreshPort;
        _catalogVisibilityPort = catalogVisibilityPort;
        _logger = logger ?? NullLogger<StudioWorkflowProvisioningService>.Instance;
    }

    public Task<WorkflowCapabilityAdmissionPlan> ResolveAsync(
        WorkflowExternalCapabilityAdmissionRequest liveRequest,
        WorkflowCapabilityAdmissionPlan? existingPlan,
        ProvisionWorkflowRequest provisioningRequest,
        CancellationToken ct)
    {
        if (existingPlan is null)
            return AdmitWithCatalogPreparationAsync(liveRequest, provisioningRequest, ct);

        var persisted = new PersistedWorkflowCapabilityAdmissionRequest(
            existingPlan,
            liveRequest.WorkflowYaml,
            liveRequest.InlineWorkflowYamls,
            liveRequest.SourceKind,
            liveRequest.ExecutionMode,
            liveRequest.WorkflowId,
            liveRequest.RevisionId);
        if (liveRequest.Access.NyxIdCallerCredential is null)
            return _admissionService.RevalidatePersistedAsync(persisted, ct);

        var refreshRequest = new RefreshPersistedWorkflowCapabilityAdmissionRequest(
            persisted,
            liveRequest.Access,
            liveRequest.ExplicitRequestConfirmations);
        return RefreshPersistedWithCatalogPreparationAsync(
            liveRequest,
            refreshRequest,
            provisioningRequest,
            ct);
    }

    private async Task<WorkflowCapabilityAdmissionPlan> RefreshPersistedWithCatalogPreparationAsync(
        WorkflowExternalCapabilityAdmissionRequest liveRequest,
        RefreshPersistedWorkflowCapabilityAdmissionRequest refreshRequest,
        ProvisionWorkflowRequest provisioningRequest,
        CancellationToken ct)
    {
        try
        {
            return await _admissionService.RefreshPersistedAsync(refreshRequest, ct);
        }
        catch (WorkflowExternalCapabilityAdmissionException exception)
            when (IsDurableAuthorizationCatalogUnavailable(liveRequest, exception))
        {
            if (!TryResolveCatalogPreparationInputs(
                    liveRequest,
                    provisioningRequest,
                    out var owner,
                    out var bearerToken) ||
                !TryResolveDurableRequiredServices(
                    refreshRequest.Persisted.Plan,
                    out var requiredServices))
            {
                throw;
            }

            await RefreshAuthorizationCatalogAsync(
                owner!,
                bearerToken!,
                requiredServices,
                ct);
            return await _admissionService.RefreshPersistedAsync(refreshRequest, ct);
        }
    }

    private async Task<WorkflowCapabilityAdmissionPlan> AdmitWithCatalogPreparationAsync(
        WorkflowExternalCapabilityAdmissionRequest admissionRequest,
        ProvisionWorkflowRequest provisioningRequest,
        CancellationToken ct)
    {
        try
        {
            return await _admissionService.AdmitAsync(admissionRequest, ct);
        }
        catch (WorkflowExternalCapabilityAdmissionException initialException)
            when (IsDurableAuthorizationCatalogUnavailable(admissionRequest, initialException))
        {
            if (!TryResolveCatalogPreparationInputs(
                    admissionRequest,
                    provisioningRequest,
                    out var owner,
                    out var bearerToken))
            {
                throw;
            }

            var interactivePlan = await _admissionService.AdmitAsync(
                new WorkflowExternalCapabilityAdmissionRequest(
                    admissionRequest.Access,
                    admissionRequest.WorkflowYaml,
                    admissionRequest.InlineWorkflowYamls,
                    admissionRequest.SourceKind,
                    ExternalCapabilityExecutionMode.Interactive,
                    admissionRequest.ExplicitRequestConfirmations,
                    admissionRequest.WorkflowId,
                    admissionRequest.RevisionId,
                    explicitRequestGrantMode: ExternalCapabilityExecutionMode.Durable),
                ct);
            if (!TryResolveDurableRequiredServices(interactivePlan, out var requiredServices))
                throw;

            await RefreshAuthorizationCatalogAsync(
                owner!,
                bearerToken!,
                requiredServices,
                ct);

            return await _admissionService.AdmitAsync(admissionRequest, ct);
        }
    }

    private async Task RefreshAuthorizationCatalogAsync(
        AuthorizationOwnerIdentity owner,
        string bearerToken,
        IReadOnlyList<NyxIdUserServiceCapabilityRef> requiredServices,
        CancellationToken ct)
    {
        NyxIdAuthorizationCatalogRefreshResult refresh;
        try
        {
            refresh = await _catalogRefreshPort!.RefreshAsync(
                owner,
                bearerToken,
                new NyxIdAuthorizationCatalogRefreshRequest(requiredServices, null),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Studio workflow durable authorization catalog refresh failed. failureType={FailureType}",
                ex.GetType().Name);
            throw new StudioMemberAutomationCatalogRefreshUnavailableException();
        }

        _logger.LogInformation(
            "Studio workflow durable authorization catalog refresh completed. status={RefreshStatus} stateVersion={StateVersion} failureCode={FailureCode}",
            refresh.Status,
            refresh.StateVersion,
            refresh.FailureCode);

        if (refresh.Status == NyxIdAuthorizationCatalogRefreshStatus.Superseded)
            throw new StudioMemberAutomationCatalogRefreshSupersededException();
        if (refresh.Status == NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable &&
            string.Equals(
                refresh.FailureCode,
                StudioMemberAutomationCatalogRouteUnresolvedException.NyxIdFailureCode,
                StringComparison.Ordinal))
        {
            throw new StudioMemberAutomationCatalogRouteUnresolvedException(
                requiredServices.Select(static service => service.UserServiceId));
        }
        if (!refresh.Success)
            throw new StudioMemberAutomationCatalogRefreshUnavailableException();

        var visibility = await _catalogVisibilityPort!
            .ResolveAsync(owner, refresh.StateVersion, ct);
        _logger.LogInformation(
            "Studio workflow durable authorization catalog visibility resolved. status={VisibilityStatus} requiredStateVersion={RequiredStateVersion} visibleStateVersion={VisibleStateVersion} failureCode={FailureCode}",
            visibility.Status,
            visibility.RequiredStateVersion,
            visibility.VisibleStateVersion,
            visibility.FailureCode);
        if (visibility.ProjectionPending)
            throw new StudioMemberAutomationProjectionPendingException(refresh.StateVersion);
        if (!visibility.Ready &&
            visibility.Status != NyxIdAuthorizationCatalogVisibilityStatus.Stale)
        {
            throw new StudioMemberAutomationCatalogRefreshUnavailableException();
        }
    }

    private bool TryResolveCatalogPreparationInputs(
        WorkflowExternalCapabilityAdmissionRequest admissionRequest,
        ProvisionWorkflowRequest provisioningRequest,
        out AuthorizationOwnerIdentity? owner,
        out string? bearerToken)
    {
        owner = null;
        bearerToken = null;
        if (_catalogRefreshPort is null || _catalogVisibilityPort is null)
            return false;

        var candidate = provisioningRequest.AuthenticatedOwner?.Owner;
        if (candidate is null ||
            !string.Equals(candidate.Authority, NyxIdAuthorizationAuthorities.NyxId, StringComparison.Ordinal) ||
            candidate.OwnerKind != AuthorizationOwnerKind.Personal ||
            string.IsNullOrWhiteSpace(candidate.OwnerSubject) ||
            !string.Equals(candidate.OwnerSubject, candidate.OwnerSubject.Trim(), StringComparison.Ordinal) ||
            !string.Equals(candidate.OwnerSubject, admissionRequest.Access.CallerId, StringComparison.Ordinal))
        {
            return false;
        }

        var parsedBearer = WorkflowCallerCredentialTokens.ParseOptional(
            provisioningRequest.ProvisioningBearerToken);
        if (!parsedBearer.IsValid)
            return false;

        owner = candidate.Clone();
        bearerToken = parsedBearer.NormalizedBearerToken;
        return true;
    }

    private static bool TryResolveDurableRequiredServices(
        WorkflowCapabilityAdmissionPlan interactivePlan,
        out IReadOnlyList<NyxIdUserServiceCapabilityRef> requiredServices)
    {
        var services = new SortedDictionary<string, NyxIdUserServiceCapabilityRef>(StringComparer.Ordinal);
        foreach (var capability in WorkflowCapabilityAdmissionPlanIntegrity.DistinctCapabilities(interactivePlan))
        {
            switch (capability.CapabilityCase)
            {
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService:
                    {
                        var service = capability.NyxIdUserService;
                        if (service.ExecutionPolicy?.AllowedExecutionModes.Contains(
                                ExternalCapabilityExecutionMode.Durable) != true ||
                            string.IsNullOrWhiteSpace(service.UserServiceId))
                        {
                            requiredServices = [];
                            return false;
                        }

                        services[service.UserServiceId] = service.Clone();
                        break;
                    }
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest:
                    {
                        var request = capability.NyxIdUserRequest;
                        if (request.ExecutionPolicy?.AllowedExecutionModes.Contains(
                                ExternalCapabilityExecutionMode.Durable) != true ||
                            string.IsNullOrWhiteSpace(request.Request.UserServiceId))
                        {
                            requiredServices = [];
                            return false;
                        }

                        services.TryAdd(
                            request.Request.UserServiceId,
                            new NyxIdUserServiceCapabilityRef
                            {
                                UserServiceId = request.Request.UserServiceId,
                            });
                        break;
                    }
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.CodeExecution:
                    {
                        var proof = capability.CodeExecution;
                        if (!proof.AllowedExecutionModes.Contains(
                                ExternalCapabilityExecutionMode.Durable) ||
                            string.IsNullOrWhiteSpace(proof.UserServiceId))
                        {
                            requiredServices = [];
                            return false;
                        }

                        services.TryAdd(
                            proof.UserServiceId,
                            new NyxIdUserServiceCapabilityRef
                            {
                                UserServiceId = proof.UserServiceId,
                                ServiceSlugSnapshot = proof.ServiceSlugSnapshot,
                            });
                        break;
                    }
                case ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector:
                    break;
                default:
                    requiredServices = [];
                    return false;
            }
        }

        requiredServices = services.Values.ToArray();
        return requiredServices.Count > 0;
    }

    private static bool IsDurableAuthorizationCatalogUnavailable(
        WorkflowExternalCapabilityAdmissionRequest request,
        WorkflowExternalCapabilityAdmissionException exception) =>
        request.ExecutionMode == ExternalCapabilityExecutionMode.Durable &&
        exception.Readiness.ExecutionMode == request.ExecutionMode &&
        exception.Readiness.Status == ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable &&
        exception.Readiness.Blockers.Count > 0 &&
        exception.Readiness.Blockers.All(static blocker =>
            blocker.Status == ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable &&
            string.Equals(blocker.Code, DurableAuthorizationUnavailableCode, StringComparison.Ordinal));
}
