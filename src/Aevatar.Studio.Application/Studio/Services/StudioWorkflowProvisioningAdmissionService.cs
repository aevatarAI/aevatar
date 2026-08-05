using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Studio.Application.Studio.Services;

internal sealed class StudioWorkflowProvisioningAdmissionService
{
    private const string DurableAuthorizationUnavailableCode =
        "DURABLE_AUTHORIZATION_UNAVAILABLE";

    private readonly IWorkflowExternalCapabilityAdmissionService _admissionService;
    private readonly INyxIdAuthorizationCatalogRefreshPort? _catalogRefreshPort;
    private readonly INyxIdAuthorizationCatalogVisibilityPort? _catalogVisibilityPort;

    public StudioWorkflowProvisioningAdmissionService(
        IWorkflowExternalCapabilityAdmissionService admissionService,
        INyxIdAuthorizationCatalogRefreshPort? catalogRefreshPort,
        INyxIdAuthorizationCatalogVisibilityPort? catalogVisibilityPort)
    {
        _admissionService = admissionService ?? throw new ArgumentNullException(nameof(admissionService));
        _catalogRefreshPort = catalogRefreshPort;
        _catalogVisibilityPort = catalogVisibilityPort;
    }

    public Task<WorkflowCapabilityAdmissionPlan> ResolveAsync(
        WorkflowExternalCapabilityAdmissionRequest liveRequest,
        WorkflowCapabilityAdmissionPlan? existingPlan,
        ProvisionWorkflowRequest provisioningRequest,
        CancellationToken ct)
    {
        if (existingPlan is null)
            return AdmitWithCatalogPreparationAsync(liveRequest, provisioningRequest, ct);

        return _admissionService.RevalidatePersistedAsync(
            new PersistedWorkflowCapabilityAdmissionRequest(
                existingPlan,
                liveRequest.WorkflowYaml,
                liveRequest.InlineWorkflowYamls,
                liveRequest.SourceKind,
                liveRequest.ExecutionMode,
                liveRequest.WorkflowId,
                liveRequest.RevisionId),
            ct);
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

            NyxIdAuthorizationCatalogRefreshResult refresh;
            try
            {
                refresh = await _catalogRefreshPort!.RefreshAsync(
                    owner!,
                    bearerToken!,
                    new NyxIdAuthorizationCatalogRefreshRequest(requiredServices, null),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new StudioMemberAutomationCatalogRefreshUnavailableException();
            }

            if (refresh.Status == NyxIdAuthorizationCatalogRefreshStatus.Superseded)
                throw new StudioMemberAutomationCatalogRefreshSupersededException();
            if (!refresh.Success)
                throw new StudioMemberAutomationCatalogRefreshUnavailableException();

            var visibility = await _catalogVisibilityPort!
                .ResolveAsync(owner!, refresh.StateVersion, ct);
            if (visibility.ProjectionPending)
                throw new StudioMemberAutomationProjectionPendingException(refresh.StateVersion);
            if (!visibility.Ready)
                throw new StudioMemberAutomationCatalogRefreshUnavailableException();

            return await _admissionService.AdmitAsync(admissionRequest, ct);
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
                        if (request.ExecutionPolicy?.Risk != NyxIdOperationRisk.ReadOnly ||
                            !request.ExecutionPolicy.AllowedExecutionModes.Contains(
                                ExternalCapabilityExecutionMode.Durable) ||
                            request.Request?.Method is not (
                                NyxIdRequestMethod.Get or
                                NyxIdRequestMethod.Head or
                                NyxIdRequestMethod.Options) ||
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
