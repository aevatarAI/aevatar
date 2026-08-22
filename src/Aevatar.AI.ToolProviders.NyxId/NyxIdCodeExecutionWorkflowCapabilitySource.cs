using System.Globalization;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdCodeExecutionWorkflowCapabilitySource(
    INyxIdApiClientFactory clientFactory,
    NyxIdToolOptions options,
    TimeProvider? timeProvider = null,
    INyxIdAuthorizationCatalogQueryPort? catalogQueryPort = null,
    ILogger<NyxIdCodeExecutionWorkflowCapabilitySource>? logger = null) :
    IExternalWorkflowCapabilitySource
{
    private const string PolicyMismatchMessage =
        "The canonical platform code execution route does not deliver the required Agent Key " +
        "and execution capability. Set forward_access_token=true, inject_delegation_token=true, " +
        "and include proxy:* and sandbox:execute in delegation_token_scope.";

    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<NyxIdCodeExecutionWorkflowCapabilitySource> _logger =
        logger ?? NullLogger<NyxIdCodeExecutionWorkflowCapabilitySource>.Instance;
    private readonly NyxIdDurableAuthorizationCatalogInspector _durableAuthorizationCatalog =
        new(
            catalogQueryPort,
            timeProvider ?? TimeProvider.System,
            logger ?? NullLogger<NyxIdCodeExecutionWorkflowCapabilitySource>.Instance);

    public ExternalWorkflowCapabilitySelector.SelectorOneofCase SelectorKind =>
        ExternalWorkflowCapabilitySelector.SelectorOneofCase.CodeExecution;

    public Task<ExternalWorkflowCapabilityDiscoveryResult> ListAsync(
        ExternalWorkflowCapabilityAccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ExternalWorkflowCapabilityDiscoveryResult());
    }

    public async Task<ExternalCapabilityReadiness> InspectAsync(
        ExternalWorkflowCapabilityAccessContext access,
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(selector);
        if (selector.SelectorCase !=
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.CodeExecution)
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.OperationSelectionRequired,
                "CODE_EXECUTION_SELECTOR_INVALID",
                "Select the canonical platform code execution capability.");
        }

        var bearerToken = access.NyxIdCallerCredential?.SourceReadableUserBearerToken ??
                          access.NyxIdCallerCredential?.ProxyDelegationToken;
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                "CODE_EXECUTION_SOURCE_CREDENTIAL_REQUIRED",
                "A NyxID caller credential authorized to read the code execution route is required.");
        }
        if (string.IsNullOrWhiteSpace(options.EffectiveTransportBaseUrl))
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.SourceStale,
                "CODE_EXECUTION_ROUTE_SOURCE_UNAVAILABLE",
                "Platform code execution route facts are currently unavailable.");
        }

        NyxIdUserServiceAuthoritySnapshot authority;
        try
        {
            authority = await new NyxIdUserServiceAuthorityReader(clientFactory)
                .ReadAsync(bearerToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Code execution readiness source failed. failureKind=source_exception exceptionType={ExceptionType}",
                exception.GetType().Name);
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.SourceStale,
                "CODE_EXECUTION_ROUTE_SOURCE_UNAVAILABLE",
                "Platform code execution route facts are currently unavailable.");
        }

        var resolution = NyxIdCodeExecutionRouteResolver.Resolve(authority);
        var source = authority.Succeeded ? BuildSource(access, authority) : null;
        if (!resolution.IsReady)
        {
            _logger.LogWarning(
                "Code execution readiness rejected. failureKind={FailureKind} canonicalCandidates={CanonicalCandidates} accessibleCandidates={AccessibleCandidates} activeCandidates={ActiveCandidates} eligibleCandidates={EligibleCandidates}",
                resolution.Kind,
                resolution.CanonicalCandidateCount,
                resolution.AccessibleCandidateCount,
                resolution.ActiveCandidateCount,
                resolution.EligibleCandidateCount);
            return ResolutionFailure(selector, executionMode, resolution, source);
        }

        var capability = BuildCapability(resolution.Service!);
        var ready = Ready(selector, executionMode, capability);
        ready.Sources.Add(source!);
        if (executionMode != ExternalCapabilityExecutionMode.Durable)
            return ready;

        var durableSource = await _durableAuthorizationCatalog.InspectAsync(
            access,
            resolution.Service!.Id,
            resolution.Service.Slug,
            cancellationToken);
        if (durableSource is null)
        {
            return Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
                "CODE_EXECUTION_DURABLE_AUTHORIZATION_UNAVAILABLE",
                "The current authorization catalog does not prove the platform code execution grant.",
                source,
                capability);
        }

        ready.Sources.Add(durableSource);
        return ready;
    }

    private ExternalCapabilitySourceStamp BuildSource(
        ExternalWorkflowCapabilityAccessContext access,
        NyxIdUserServiceAuthoritySnapshot authority)
    {
        var observedAt = _timeProvider.GetUtcNow();
        var routeComponents = authority.Routes.Value!.Services
            .OrderBy(static service => service.Id, StringComparer.Ordinal)
            .SelectMany(static service => new[]
            {
                "route",
                service.Id,
                service.Slug,
                service.CatalogServiceId,
                service.IsActive.ToString(CultureInfo.InvariantCulture),
                service.CredentialSource.Kind.ToString(),
                service.CredentialSource.OrganizationId,
                service.CredentialSource.Allowed.ToString(CultureInfo.InvariantCulture),
                service.ForwardAccessToken?.ToString(CultureInfo.InvariantCulture),
                service.InjectDelegationToken?.ToString(CultureInfo.InvariantCulture),
                service.DelegationTokenScope,
                service.AutoConnected.ToString(CultureInfo.InvariantCulture),
            });
        var executionComponents = authority.ExecutionInventory.Value!.Services
            .OrderBy(static service => service.Id, StringComparer.Ordinal)
            .SelectMany(static service => new[]
            {
                "execution",
                service.Id,
                service.Slug,
                service.IsActive.ToString(CultureInfo.InvariantCulture),
                service.Connected.ToString(CultureInfo.InvariantCulture),
                service.CredentialSource.Kind.ToString(),
                service.CredentialSource.OrganizationId,
                service.CredentialSource.Allowed.ToString(CultureInfo.InvariantCulture),
                service.CatalogServiceId,
                service.CatalogServiceSlug,
                service.CredentialStatus.ToString(),
                service.NodeId,
                service.NodeStatus.ToString(),
            });
        return new ExternalCapabilitySourceStamp
        {
            SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
            SourceId = $"nyxid-user-services:caller:{access.CallerId}",
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
            FreshUntil = Timestamp.FromDateTimeOffset(observedAt + FreshnessWindow),
            ContentDigest = ExternalWorkflowCapabilityContractDigest.Compute(
                routeComponents.Concat(executionComponents)),
        };
    }

    private static ExternalWorkflowCapabilityRef BuildCapability(NyxIdUserService service)
    {
        var catalogServiceId = service.CatalogServiceId
            ?? throw new InvalidOperationException("Resolved code execution route has no catalog identity.");
        var proof = new CodeExecutionCapabilityRef
        {
            UserServiceId = service.Id,
            ServiceSlugSnapshot = service.Slug,
            CatalogServiceId = catalogServiceId,
        };
        proof.ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeCodeExecutionCapabilityDigest(
                proof.UserServiceId,
                proof.ServiceSlugSnapshot,
                proof.CatalogServiceId);
        proof.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        proof.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
        return new ExternalWorkflowCapabilityRef { CodeExecution = proof };
    }

    private ExternalCapabilityReadiness ResolutionFailure(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        NyxIdCodeExecutionRouteResolution resolution,
        ExternalCapabilitySourceStamp? source) =>
        resolution.Kind switch
        {
            NyxIdCodeExecutionRouteResolutionKind.Missing => Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
                "CODE_EXECUTION_ROUTE_MISSING",
                "The canonical platform code execution route is missing.",
                source,
                remediationAction: ExternalCapabilityRemediationActionKind.RegisterService,
                remediationLabel: "Connect the platform code service"),
            NyxIdCodeExecutionRouteResolutionKind.Inactive => Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "CODE_EXECUTION_ROUTE_INACTIVE",
                "The canonical platform code execution route is inactive.",
                source,
                remediationAction: ExternalCapabilityRemediationActionKind.ConnectCredential,
                remediationLabel: "Reactivate the platform code service"),
            NyxIdCodeExecutionRouteResolutionKind.PolicyMismatch => Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "CODE_EXECUTION_ROUTE_POLICY_MISMATCH",
                PolicyMismatchMessage,
                source,
                remediationAction: ExternalCapabilityRemediationActionKind.ConnectCredential,
                remediationLabel: "Update the platform code service identity settings"),
            NyxIdCodeExecutionRouteResolutionKind.ExecutionNotReady => Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
                "CODE_EXECUTION_ROUTE_NOT_READY",
                "The canonical platform code execution service is not executable for this caller.",
                source,
                remediationAction: ExternalCapabilityRemediationActionKind.ConnectCredential,
                remediationLabel: "Restore the platform code service credential or node"),
            NyxIdCodeExecutionRouteResolutionKind.Ambiguous => Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ContractDrift,
                "CODE_EXECUTION_ROUTE_AMBIGUOUS",
                "Multiple canonical platform code execution routes are eligible.",
                source,
                remediationAction: ExternalCapabilityRemediationActionKind.ConfigureConnector,
                remediationLabel: "Keep exactly one platform code service"),
            NyxIdCodeExecutionRouteResolutionKind.AccessDenied => Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                "CODE_EXECUTION_ROUTE_ACCESS_DENIED",
                "The caller cannot access the canonical platform code execution route.",
                source,
                remediationAction: ExternalCapabilityRemediationActionKind.RequestAccess,
                remediationLabel: "Request access to the platform code service"),
            _ => Failure(
                selector,
                executionMode,
                ExternalCapabilityReadinessStatus.SourceStale,
                "CODE_EXECUTION_ROUTE_SOURCE_UNAVAILABLE",
                "Platform code execution route facts are currently unavailable.",
                source),
        };

    private static ExternalCapabilityReadiness Ready(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        ExternalWorkflowCapabilityRef capability) =>
        new()
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedSelector = selector.Clone(),
            SelectedCapability = capability,
        };

    private ExternalCapabilityReadiness Failure(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadinessStatus status,
        string code,
        string message,
        ExternalCapabilitySourceStamp? source = null,
        ExternalWorkflowCapabilityRef? capability = null,
        ExternalCapabilityRemediationActionKind remediationAction =
            ExternalCapabilityRemediationActionKind.RefreshSource,
        string remediationLabel = "Restore platform code route")
    {
        var result = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = status,
            SelectedSelector = selector.Clone(),
            SelectedCapability = capability?.Clone(),
        };
        result.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = status,
            Code = code,
            SafeMessage = message,
        });
        result.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = remediationAction,
            Label = remediationLabel,
            TrustedLocator = TrustedLocator(),
        });
        if (source is not null)
            result.Sources.Add(source);
        return result;
    }

    private string TrustedLocator() =>
        string.IsNullOrWhiteSpace(options.EffectiveApiBaseUrl)
            ? string.Empty
            : options.EffectiveApiBaseUrl.TrimEnd('/');
}
