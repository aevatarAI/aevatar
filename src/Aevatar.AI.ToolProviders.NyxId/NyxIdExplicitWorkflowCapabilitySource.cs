using System.Globalization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdExplicitWorkflowCapabilitySource(
    NyxIdApiClient client,
    NyxIdToolOptions options,
    TimeProvider? timeProvider = null) : IExternalWorkflowCapabilitySource
{
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
        if (string.IsNullOrWhiteSpace(access.NyxIdCallerBearerToken))
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                "NYXID_CALLER_ACCESS_REQUIRED", "A caller NyxID credential is required.");
        }
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.SourceStale,
                "NYXID_SOURCE_UNAVAILABLE", "NyxID UserService facts are currently unavailable.");
        }

        NyxIdApiAccessResult<NyxIdUserServices> inventory;
        try
        {
            inventory = NyxIdApiAccessResponseParser.ParseUserServices(
                await client.ListServicesAsync(access.NyxIdCallerBearerToken, cancellationToken));
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

        var capability = BuildCapability(request, service.Slug);
        if (executionMode == ExternalCapabilityExecutionMode.Durable)
        {
            return Failure(
                selector, executionMode, ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable,
                "DURABLE_AUTHORIZATION_UNAVAILABLE",
                "The current authorization catalog does not prove this durable UserService grant.", source, capability);
        }

        var ready = new ExternalCapabilityReadiness
        {
            ExecutionMode = executionMode,
            Status = ExternalCapabilityReadinessStatus.Ready,
            SelectedSelector = new ExternalWorkflowCapabilitySelector { NyxIdRequest = request },
            SelectedCapability = capability,
        };
        ready.Sources.Add(source);
        return ready;
    }

    private ExternalCapabilitySourceStamp BuildSource(
        ExternalWorkflowCapabilityAccessContext access,
        IReadOnlyList<NyxIdUserService> services)
    {
        var observedAt = _timeProvider.GetUtcNow();
        var components = services
            .OrderBy(static service => service.Id, StringComparer.Ordinal)
            .SelectMany(static service => new[]
            {
                service.Id, service.Slug, service.IsActive.ToString(CultureInfo.InvariantCulture),
                service.CredentialSource.Kind.ToString(), service.CredentialSource.OrganizationId,
                service.CredentialSource.Allowed.ToString(CultureInfo.InvariantCulture),
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
        var policy = new NyxIdOperationExecutionPolicy
        {
            Risk = request.Method switch
            {
                NyxIdRequestMethod.Get or NyxIdRequestMethod.Head or NyxIdRequestMethod.Options =>
                    NyxIdOperationRisk.ReadOnly,
                NyxIdRequestMethod.Delete => NyxIdOperationRisk.Destructive,
                _ => NyxIdOperationRisk.Write,
            },
            Approval = request.Method == NyxIdRequestMethod.Delete
                ? NyxIdOperationApproval.Required
                : NyxIdOperationApproval.None,
            EnforcementOwner = NyxIdOperationEnforcementOwner.Aevatar,
        };
        policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        if (request.Method != NyxIdRequestMethod.Delete)
            policy.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);

        var digest = ExternalWorkflowCapabilityContractDigest.Compute(
            "nyxid-explicit-request/v1", request.UserServiceId, serviceSlug,
            NyxIdRequestSelectorContract.MethodName(request.Method), request.PathTemplate,
            string.Join("\n", request.QueryParameters), string.Join("\n", request.HeaderParameters),
            ((int)request.BodyMode).ToString(CultureInfo.InvariantCulture),
            ((int)request.ResponseMode).ToString(CultureInfo.InvariantCulture));
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

    private static ExternalCapabilityReadiness Failure(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadinessStatus status,
        string code,
        string message,
        ExternalCapabilitySourceStamp? source = null,
        ExternalWorkflowCapabilityRef? capability = null)
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
            ActionKind = status == ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable
                ? ExternalCapabilityRemediationActionKind.UseInteractiveExecution
                : ExternalCapabilityRemediationActionKind.RefreshSource,
            Label = status == ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable
                ? "Use interactive execution"
                : "Refresh NyxID services",
        });
        if (source is not null)
            result.Sources.Add(source);
        return result;
    }
}
