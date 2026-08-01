using System.Globalization;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdExplicitWorkflowCapabilitySource(
    NyxIdApiClient client,
    NyxIdToolOptions options,
    TimeProvider? timeProvider = null,
    INyxIdAuthorizationCatalogQueryPort? catalogQueryPort = null,
    ILogger<NyxIdExplicitWorkflowCapabilitySource>? logger = null) : IExternalWorkflowCapabilitySource
{
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly NyxIdDurableAuthorizationCatalogInspector _durableAuthorizationCatalog =
        new(
            catalogQueryPort,
            timeProvider ?? TimeProvider.System,
            logger ?? NullLogger<NyxIdExplicitWorkflowCapabilitySource>.Instance);

    public ExternalWorkflowCapabilitySelector.SelectorOneofCase SelectorKind =>
        ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest;

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
        if (selector.SelectorCase != ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest ||
            !NyxIdRequestSelectorContract.TryNormalize(selector.NyxIdRequest, out var request, out _))
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.OperationSelectionRequired,
                "NYXID_REQUEST_CONTRACT_INVALID", "Provide one valid exact NyxID request contract.");
        }
        var sourceReadableBearerToken = access.NyxIdCallerCredential?.SourceReadableUserBearerToken;
        if (string.IsNullOrWhiteSpace(sourceReadableBearerToken))
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                "NYXID_ADMISSION_SOURCE_CREDENTIAL_REQUIRED",
                "A source-readable caller NyxID credential is required.");
        }
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.SourceStale,
                "NYXID_SOURCE_UNAVAILABLE", "NyxID UserService facts are currently unavailable.");
        }

        NyxIdApiAccessResult<NyxIdUserServiceKeys> inventory;
        try
        {
            inventory = NyxIdApiAccessResponseParser.ParseUserServiceKeys(
                await client.ListServicesAsync(sourceReadableBearerToken, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.SourceStale,
                "NYXID_SOURCE_UNAVAILABLE", "NyxID UserService facts are currently unavailable.");
        }

        if (!inventory.Succeeded)
        {
            var denied = inventory.Failure?.Kind is
                NyxIdApiAccessFailureKind.Unauthorized or NyxIdApiAccessFailureKind.Forbidden;
            return Failure(
                selector, executionMode,
                denied ? ExternalCapabilityReadinessStatus.ServiceAccessDenied : ExternalCapabilityReadinessStatus.SourceStale,
                denied ? "NYXID_CALLER_ACCESS_REQUIRED" : "NYXID_SOURCE_UNAVAILABLE",
                denied ? "The caller cannot inspect NyxID UserServices." : "NyxID UserService facts are currently unavailable.");
        }

        var services = inventory.Value!.Services;
        var source = BuildSource(access, services);
        var service = services.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, request.UserServiceId, StringComparison.Ordinal));
        if (service is null)
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
                "USER_SERVICE_NOT_VISIBLE", "The selected NyxID UserService is not visible to the current caller.", source);
        }
        if (!service.IsActive)
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
                "USER_SERVICE_INACTIVE", "The selected NyxID UserService is inactive.", source);
        }
        if (!service.CredentialSource.Allowed)
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                "USER_SERVICE_ACCESS_DENIED", "The selected NyxID UserService is not allowed for this caller.", source);
        }
        if (service.NodeId is null &&
            service.CredentialStatus != NyxIdUserServiceCredentialStatus.Active)
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.CredentialConnectionRequired,
                "USER_SERVICE_CREDENTIAL_NOT_READY",
                "The selected NyxID UserService credential is not ready.",
                source,
                remediationAction: ExternalCapabilityRemediationActionKind.ConnectCredential,
                remediationLabel: "Connect NyxID credential");
        }
        if (service.NodeStatus == NyxIdUserServiceNodeStatus.Inaccessible)
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                "USER_SERVICE_NODE_ACCESS_DENIED",
                "The selected NyxID UserService node is not accessible to this caller.",
                source,
                remediationAction: ExternalCapabilityRemediationActionKind.RequestAccess,
                remediationLabel: "Request NyxID node access");
        }
        if (service.NodeId is not null && service.NodeStatus != NyxIdUserServiceNodeStatus.Online)
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.NodeUnavailable,
                "USER_SERVICE_NODE_UNAVAILABLE",
                "The selected NyxID UserService node is unavailable.",
                source,
                remediationAction: ExternalCapabilityRemediationActionKind.RestoreNode,
                remediationLabel: "Restore NyxID node");
        }

        var capability = BuildCapability(request, service.Slug);
        if (executionMode == ExternalCapabilityExecutionMode.Durable)
        {
            if (capability.NyxIdUserRequest.ExecutionPolicy.Risk != NyxIdOperationRisk.ReadOnly ||
                !capability.NyxIdUserRequest.ExecutionPolicy.AllowedExecutionModes.Contains(
                    ExternalCapabilityExecutionMode.Durable))
            {
                return Failure(
                    selector, executionMode, ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
                    "NYXID_EXPLICIT_REQUEST_INTERACTIVE_REQUIRED",
                    "This explicit request can only be admitted for interactive execution.", source, capability);
            }

            var durableAuthorizationSource = await _durableAuthorizationCatalog.InspectAsync(
                access,
                request.UserServiceId,
                service.Slug,
                cancellationToken);
            if (durableAuthorizationSource is null)
            {
                return Failure(
                    selector, executionMode, ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
                    "DURABLE_AUTHORIZATION_UNAVAILABLE",
                    "The current authorization catalog does not prove this durable UserService grant.", source, capability);
            }

            var durableReady = Ready(selector, executionMode, capability);
            durableReady.Sources.Add(source);
            durableReady.Sources.Add(durableAuthorizationSource);
            return durableReady;
        }

        var ready = Ready(selector, executionMode, capability);
        ready.Sources.Add(source);
        return ready;
    }

    private ExternalCapabilitySourceStamp BuildSource(
        ExternalWorkflowCapabilityAccessContext access,
        IReadOnlyList<NyxIdUserServiceKey> services)
    {
        var observedAt = _timeProvider.GetUtcNow();
        var components = services
            .OrderBy(static service => service.Id, StringComparer.Ordinal)
            .SelectMany(static service => new[]
            {
                service.Id, service.Slug, service.IsActive.ToString(CultureInfo.InvariantCulture),
                service.CredentialSource.Kind.ToString(), service.CredentialSource.OrganizationId,
                service.CredentialSource.Allowed.ToString(CultureInfo.InvariantCulture),
                service.CredentialStatus.ToString(), service.NodeId, service.NodeStatus.ToString(),
            });
        return new ExternalCapabilitySourceStamp
        {
            SourceKind = ExternalCapabilitySourceKind.NyxIdUserServices,
            SourceId = $"nyxid-user-services:caller:{access.CallerId}",
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
            FreshUntil = Timestamp.FromDateTimeOffset(observedAt + FreshnessWindow),
            ContentDigest = ExternalWorkflowCapabilityContractDigest.Compute(components),
        };
    }

    private static ExternalWorkflowCapabilityRef BuildCapability(
        NyxIdRequestSelector request,
        string serviceSlug)
    {
        var risk = request.Method switch
        {
            NyxIdRequestMethod.Get or NyxIdRequestMethod.Head or NyxIdRequestMethod.Options =>
                NyxIdOperationRisk.ReadOnly,
            NyxIdRequestMethod.Delete => NyxIdOperationRisk.Destructive,
            _ => NyxIdOperationRisk.Write,
        };
        var policy = new NyxIdOperationExecutionPolicy
        {
            Risk = risk,
            Approval = risk == NyxIdOperationRisk.ReadOnly
                ? NyxIdOperationApproval.None
                : NyxIdOperationApproval.Required,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
        };
        policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        if (risk == NyxIdOperationRisk.ReadOnly)
            policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);

        var requestDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdRequestContractDigest(request);
        var digest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdExplicitRequestProofDigest(requestDigest, serviceSlug);
        return new ExternalWorkflowCapabilityRef
        {
            NyxIdUserRequest = new NyxIdUserRequestCapabilityRef
            {
                Request = request.Clone(),
                ServiceSlugSnapshot = serviceSlug,
                ContractDigest = digest,
                ExecutionPolicy = policy,
            },
        };
    }

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

    private static ExternalCapabilityReadiness Failure(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadinessStatus status,
        string code,
        string message,
        ExternalCapabilitySourceStamp? source = null,
        ExternalWorkflowCapabilityRef? capability = null,
        ExternalCapabilityRemediationActionKind remediationAction =
            ExternalCapabilityRemediationActionKind.Unspecified,
        string? remediationLabel = null)
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
        var durableAuthorizationUnavailable =
            status == ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable;
        result.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = remediationAction != ExternalCapabilityRemediationActionKind.Unspecified
                ? remediationAction
                : durableAuthorizationUnavailable
                    ? ExternalCapabilityRemediationActionKind.UseInteractiveExecution
                    : ExternalCapabilityRemediationActionKind.RefreshSource,
            Label = remediationLabel ?? (durableAuthorizationUnavailable
                ? "Use interactive execution"
                : "Refresh NyxID services"),
        });
        if (source is not null)
            result.Sources.Add(source);
        return result;
    }
}
