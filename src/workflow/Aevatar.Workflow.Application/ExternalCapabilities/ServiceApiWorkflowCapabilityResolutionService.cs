using System.Text.RegularExpressions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Workflow.Application.ExternalCapabilities;

public sealed partial class ServiceApiWorkflowCapabilityResolutionService(
    IExternalWorkflowCapabilityListPort capabilityListPort,
    IManagedCodexServiceApiSkillDiscoveryPort managedDiscoveryPort,
    IExternalWorkflowCapabilityReadinessPort readinessPort,
    IServiceApiCapabilityFallbackPort fallbackPort) :
    IServiceApiWorkflowCapabilityDiscoveryPort
{
    public async Task<ServiceApiWorkflowCapabilityDiscoveryResult> DiscoverAsync(
        DiscoverServiceApiWorkflowCapabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAccess(request.Access);
        ValidateAuthority(request.Access, request.CallerAuthority);
        if (request.ExecutionMode == ExternalCapabilityExecutionMode.Unspecified)
            throw new InvalidOperationException("Service API capability execution mode is required.");

        var normalizedCapabilityKey = NormalizeRequiredCapabilityKey(request.CapabilityKey);
        var inventory = await capabilityListPort.ListAsync(
                new ListExternalWorkflowCapabilitiesRequest(request.Access),
                cancellationToken)
            .ConfigureAwait(false);
        var input = new ServiceApiSkillDiscoveryInput
        {
            CallerAuthority = request.CallerAuthority.Clone(),
            ScopeId = request.Access.ScopeId,
            CallerId = request.Access.CallerId,
            TargetUserServiceId = Require(request.TargetUserServiceId, "target_user_service_id"),
            ServiceSlugSnapshot = request.ServiceSlugSnapshot?.Trim() ?? string.Empty,
            ServiceLabelSnapshot = request.ServiceLabelSnapshot?.Trim() ?? string.Empty,
            NormalizedCapabilityKey = normalizedCapabilityKey,
            ManagedDiscoveryPolicyVersion = Require(
                request.ManagedDiscoveryPolicyVersion,
                "managed_discovery_policy_version"),
            AdmissionPolicyVersion = Require(
                request.AdmissionPolicyVersion,
                "admission_policy_version"),
            CapabilityFingerprint = ExternalWorkflowCapabilityContractDigest.Compute(normalizedCapabilityKey),
            WorkflowId = request.WorkflowId?.Trim() ?? string.Empty,
            MemberId = request.MemberId?.Trim() ?? string.Empty,
            PublishedServiceId = request.PublishedServiceId?.Trim() ?? string.Empty,
        };
        input.DescriptorInventory.Add(
            inventory.Capabilities.Select(static descriptor => descriptor.Clone()));
        return await ResolveAsync(request.Access, input, request.ExecutionMode, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<ServiceApiWorkflowCapabilityDiscoveryResult> RetryAfterRemediationAsync(
        ExternalWorkflowCapabilityAccessContext access,
        ServiceApiCapabilityResolutionRetry retry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(retry.DiscoveryInput);
        ValidateAccess(access);
        ValidateBuiltInput(retry.DiscoveryInput);
        if (retry.ExecutionMode == ExternalCapabilityExecutionMode.Unspecified)
            throw new InvalidOperationException("Service API capability execution mode is required.");
        if (!string.Equals(access.ScopeId, retry.DiscoveryInput.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(access.CallerId, retry.DiscoveryInput.CallerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Service API capability retry authority does not match the original resolution authority.");
        }
        ValidateAuthority(access, retry.DiscoveryInput.CallerAuthority);

        return ResolveAsync(
            access,
            retry.DiscoveryInput.Clone(),
            retry.ExecutionMode,
            cancellationToken);
    }

    private async Task<ServiceApiWorkflowCapabilityDiscoveryResult> ResolveAsync(
        ExternalWorkflowCapabilityAccessContext access,
        ServiceApiSkillDiscoveryInput input,
        ExternalCapabilityExecutionMode executionMode,
        CancellationToken cancellationToken)
    {
        ValidateBuiltInput(input);
        var descriptor = ResolveExactOperationDescriptor(input);
        if (descriptor is not null)
        {
            var readiness = await InspectAsync(
                    access,
                    input,
                    descriptor.Selector,
                    executionMode,
                    cancellationToken)
                .ConfigureAwait(false);
            if (readiness.Status != ExternalCapabilityReadinessStatus.Ready)
                return ReadinessRequired(input, executionMode, readiness);
            EnsureReadinessSelector(descriptor.Selector, readiness);
            return ResolvedOperation(descriptor);
        }

        var managedResult = await managedDiscoveryPort.DiscoverAsync(
                new ManagedCodexServiceApiSkillDiscoveryRequest(access, input.Clone()),
                cancellationToken)
            .ConfigureAwait(false);
        return managedResult.ResultCase switch
        {
            ManagedCodexServiceApiSkillDiscoveryResult.ResultOneofCase.ReliableSkill =>
                await ResolveReliableSkillAsync(
                    access,
                    input,
                    executionMode,
                    managedResult.ReliableSkill,
                    cancellationToken),
            ManagedCodexServiceApiSkillDiscoveryResult.ResultOneofCase.NoReliableApiSkill =>
                await ResolveFallbackAsync(
                    access,
                    input,
                    executionMode,
                    managedResult.NoReliableApiSkill,
                    cancellationToken),
            _ => throw new InvalidOperationException(
                "Managed Service API skill discovery returned no typed result."),
        };
    }

    private async Task<ServiceApiWorkflowCapabilityDiscoveryResult> ResolveReliableSkillAsync(
        ExternalWorkflowCapabilityAccessContext access,
        ServiceApiSkillDiscoveryInput input,
        ExternalCapabilityExecutionMode executionMode,
        ReliableServiceApiSkillCandidate candidate,
        CancellationToken cancellationToken)
    {
        var selector = candidate.RequestShape?.Selector ??
                       throw new InvalidOperationException(
                           "Verified Service API skill returned no admitted request shape.");
        if (!string.Equals(selector.UserServiceId, input.TargetUserServiceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Verified Service API skill returned a request shape for a different UserService.");
        }

        var workflowSelector = new ExternalWorkflowCapabilitySelector
        {
            NyxIdRequest = selector.Clone(),
        };
        var readiness = await InspectAsync(
                access,
                input,
                workflowSelector,
                executionMode,
                cancellationToken)
            .ConfigureAwait(false);
        if (readiness.Status != ExternalCapabilityReadinessStatus.Ready)
            return ReadinessRequired(input, executionMode, readiness);
        EnsureReadinessSelector(workflowSelector, readiness);
        if (readiness.SelectedCapability?.CapabilityCase !=
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest ||
            readiness.SelectedCapability.NyxIdUserRequest.Request is null)
        {
            throw new InvalidOperationException(
                "External capability readiness returned no admitted NyxID request contract.");
        }

        var provenance = new ExactOrnnApiSkillProvenance
        {
            CanonicalName = candidate.CanonicalName,
            Guid = candidate.Guid,
            LiteralVersion = candidate.LiteralVersion,
            SkillHash = candidate.SkillHash,
            PublisherId = candidate.PublisherId,
        };
        provenance.Evidence.Add(candidate.Evidence.Select(static evidence => evidence.Clone()));
        return new ServiceApiWorkflowCapabilityDiscoveryResult
        {
            Resolution = new ServiceApiCapabilityResolution
            {
                NyxidRequest = new ResolvedNyxIdRequest
                {
                    OrnnSkill = provenance,
                    UserServiceId = input.TargetUserServiceId,
                    RequestShape = new AdmittedNyxIdRequestShape
                    {
                        Selector = readiness.SelectedCapability.NyxIdUserRequest.Request.Clone(),
                    },
                    AdmissionPolicyVersion = input.AdmissionPolicyVersion,
                },
            },
        };
    }

    private async Task<ServiceApiWorkflowCapabilityDiscoveryResult> ResolveFallbackAsync(
        ExternalWorkflowCapabilityAccessContext access,
        ServiceApiSkillDiscoveryInput input,
        ExternalCapabilityExecutionMode executionMode,
        NoReliableServiceApiSkill noReliableApiSkill,
        CancellationToken cancellationToken)
    {
        if (noReliableApiSkill.Reason == ServiceApiNoReliableSkillReason.Unspecified)
        {
            throw new InvalidOperationException(
                "Managed Service API skill discovery returned an invalid no-reliable result.");
        }

        var result = await fallbackPort.ResolveAsync(
                new ResolveServiceApiCapabilityFallbackRequest(
                    access,
                    input.Clone(),
                    noReliableApiSkill.Clone(),
                    executionMode),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ResultCase != ServiceApiWorkflowCapabilityDiscoveryResult.ResultOneofCase.Resolution ||
            result.Resolution is null ||
            result.Resolution.ResultCase is not (
                ServiceApiCapabilityResolution.ResultOneofCase.NyxidRequest or
                ServiceApiCapabilityResolution.ResultOneofCase.FallbackExhausted))
        {
            throw new InvalidOperationException(
                "Service API capability fallback returned an invalid terminal resolution.");
        }

        if (result.Resolution.ResultCase == ServiceApiCapabilityResolution.ResultOneofCase.NyxidRequest &&
            (!string.Equals(
                 result.Resolution.NyxidRequest.UserServiceId,
                 input.TargetUserServiceId,
                 StringComparison.Ordinal) ||
             result.Resolution.NyxidRequest.ContractSourceCase !=
             ResolvedNyxIdRequest.ContractSourceOneofCase.OfficialWeb ||
             result.Resolution.NyxidRequest.RequestShape?.Selector is null ||
             !string.Equals(
                 result.Resolution.NyxidRequest.RequestShape.Selector.UserServiceId,
                 input.TargetUserServiceId,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 result.Resolution.NyxidRequest.AdmissionPolicyVersion,
                 input.AdmissionPolicyVersion,
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Service API capability fallback returned an unauthorized request resolution.");
        }

        if (result.Resolution.ResultCase == ServiceApiCapabilityResolution.ResultOneofCase.FallbackExhausted)
            return result.Clone();

        var fallbackRequest = result.Resolution.NyxidRequest;
        var workflowSelector = new ExternalWorkflowCapabilitySelector
        {
            NyxIdRequest = fallbackRequest.RequestShape.Selector.Clone(),
        };
        var readiness = await InspectAsync(
                access,
                input,
                workflowSelector,
                executionMode,
                cancellationToken)
            .ConfigureAwait(false);
        if (readiness.Status != ExternalCapabilityReadinessStatus.Ready)
            return ReadinessRequired(input, executionMode, readiness);
        if (readiness.SelectedCapability?.CapabilityCase !=
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest ||
            readiness.SelectedCapability.NyxIdUserRequest.Request is null)
        {
            throw new InvalidOperationException(
                "External capability readiness returned no admitted NyxID request contract.");
        }

        return new ServiceApiWorkflowCapabilityDiscoveryResult
        {
            Resolution = new ServiceApiCapabilityResolution
            {
                NyxidRequest = new ResolvedNyxIdRequest
                {
                    OfficialWeb = fallbackRequest.OfficialWeb.Clone(),
                    UserServiceId = input.TargetUserServiceId,
                    RequestShape = new AdmittedNyxIdRequestShape
                    {
                        Selector = readiness.SelectedCapability.NyxIdUserRequest.Request.Clone(),
                    },
                    AdmissionPolicyVersion = input.AdmissionPolicyVersion,
                },
            },
        };
    }

    private async Task<ExternalCapabilityReadiness> InspectAsync(
        ExternalWorkflowCapabilityAccessContext access,
        ServiceApiSkillDiscoveryInput input,
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityExecutionMode executionMode,
        CancellationToken cancellationToken)
    {
        var readiness = await readinessPort.InspectAsync(
                new InspectExternalWorkflowCapabilityReadinessRequest(
                    access,
                    selector.Clone(),
                    executionMode),
                cancellationToken)
            .ConfigureAwait(false);
        EnsureReadinessSelector(selector, readiness);
        if (readiness.Status != ExternalCapabilityReadinessStatus.Ready)
            EnsureExecutableHandoff(readiness);
        return readiness;
    }

    private static ServiceApiWorkflowCapabilityDiscoveryResult ReadinessRequired(
        ServiceApiSkillDiscoveryInput input,
        ExternalCapabilityExecutionMode executionMode,
        ExternalCapabilityReadiness readiness) =>
        new()
        {
            ReadinessRequired = new ServiceApiCapabilityReadinessHandoff
            {
                Readiness = readiness.Clone(),
                Retry = new ServiceApiCapabilityResolutionRetry
                {
                    DiscoveryInput = input.Clone(),
                    ExecutionMode = executionMode,
                },
            },
        };

    private static ExternalWorkflowCapabilityDescriptor? ResolveExactOperationDescriptor(
        ServiceApiSkillDiscoveryInput input)
    {
        var matches = input.DescriptorInventory
            .Where(descriptor =>
                descriptor.Selector?.SelectorCase ==
                ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation &&
                string.Equals(
                    descriptor.Selector.NyxIdOperation.UserServiceId,
                    input.TargetUserServiceId,
                    StringComparison.Ordinal) &&
                DescriptorCapabilityMatches(input, descriptor))
            .OrderBy(static descriptor => descriptor.Selector.NyxIdOperation.EndpointId, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                "Multiple exact NyxID operation descriptors match the capability key.");
        }

        return matches.Length == 1 ? matches[0].Clone() : null;
    }

    private static bool DescriptorCapabilityMatches(
        ServiceApiSkillDiscoveryInput input,
        ExternalWorkflowCapabilityDescriptor descriptor)
    {
        var normalized = NormalizeCapabilityKey(descriptor.CapabilityKey);
        return normalized.Length > 0 &&
               string.Equals(normalized, input.NormalizedCapabilityKey, StringComparison.Ordinal) &&
               string.Equals(
                   ExternalWorkflowCapabilityContractDigest.Compute(normalized),
                   input.CapabilityFingerprint,
                   StringComparison.Ordinal);
    }

    private static ServiceApiWorkflowCapabilityDiscoveryResult ResolvedOperation(
        ExternalWorkflowCapabilityDescriptor descriptor) =>
        new()
        {
            Resolution = new ServiceApiCapabilityResolution
            {
                NyxidOperation = new ResolvedNyxIdOperation
                {
                    Selector = descriptor.Selector.NyxIdOperation.Clone(),
                    Descriptor_ = descriptor.Clone(),
                },
            },
        };

    private static void ValidateBuiltInput(ServiceApiSkillDiscoveryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Require(input.ScopeId, "scope_id");
        Require(input.CallerId, "caller_id");
        Require(input.TargetUserServiceId, "target_user_service_id");
        var normalized = NormalizeRequiredCapabilityKey(input.NormalizedCapabilityKey);
        if (!string.Equals(normalized, input.NormalizedCapabilityKey, StringComparison.Ordinal))
            throw new InvalidOperationException("normalized_capability_key is not canonical.");
        if (!string.Equals(
                input.CapabilityFingerprint,
                ExternalWorkflowCapabilityContractDigest.Compute(normalized),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "capability_fingerprint does not match normalized_capability_key.");
        }
        if (!Sha256HexPattern().IsMatch(input.CapabilityFingerprint))
            throw new InvalidOperationException("capability_fingerprint is invalid.");
        Require(input.ManagedDiscoveryPolicyVersion, "managed_discovery_policy_version");
        Require(input.AdmissionPolicyVersion, "admission_policy_version");
    }

    private static void ValidateAccess(ExternalWorkflowCapabilityAccessContext access)
    {
        ArgumentNullException.ThrowIfNull(access);
        Require(access.ScopeId, "scope_id");
        Require(access.CallerId, "caller_id");
    }

    private static void ValidateAuthority(
        ExternalWorkflowCapabilityAccessContext access,
        ExternalCapabilityAuthorizationOwner authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        Require(authority.Authority, "caller_authority.authority");
        Require(authority.OwnerSubject, "caller_authority.owner_subject");
        if (authority.OwnerKind == ExternalCapabilityAuthorizationOwnerKind.Unspecified)
            throw new InvalidOperationException("caller_authority.owner_kind is required.");
        if (authority.OwnerKind == ExternalCapabilityAuthorizationOwnerKind.Personal &&
            !string.Equals(authority.OwnerSubject, access.CallerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Caller authority does not match the authenticated caller identity.");
        }
    }

    private static void EnsureReadinessSelector(
        ExternalWorkflowCapabilitySelector selector,
        ExternalCapabilityReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        if (readiness.SelectedSelector is null || !readiness.SelectedSelector.Equals(selector))
        {
            throw new InvalidOperationException(
                "External capability readiness does not match the resolved selector.");
        }
    }

    private static void EnsureExecutableHandoff(ExternalCapabilityReadiness readiness)
    {
        if (readiness.Blockers.Count == 0 ||
            readiness.Remediations.Count == 0 ||
            readiness.Remediations.Any(static remediation =>
                remediation.ActionKind == ExternalCapabilityRemediationActionKind.Unspecified))
        {
            throw new InvalidOperationException(
                "External capability readiness did not provide an executable remediation handoff.");
        }
    }

    private static string NormalizeRequiredCapabilityKey(string? value)
    {
        var normalized = NormalizeCapabilityKey(value);
        return Require(normalized, "capability_key");
    }

    private static string NormalizeCapabilityKey(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WhitespacePattern().Replace(value.Trim(), " ").ToLowerInvariant();

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is required.")
            : value.Trim();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256HexPattern();
}
