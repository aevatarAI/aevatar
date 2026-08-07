using System.Text.RegularExpressions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.Workflow.Application.ExternalCapabilities;

public sealed partial class ServiceApiWorkflowCapabilityDiscoveryService(
    IManagedCodexServiceApiSkillDiscoveryExecutor managedDiscoveryExecutor,
    IExactServiceApiSkillVerifier exactSkillVerifier,
    IExternalWorkflowCapabilityReadinessPort readinessPort) :
    IServiceApiWorkflowCapabilityDiscoveryPort
{
    public async Task<ServiceApiWorkflowCapabilityDiscoveryResult> DiscoverAsync(
        DiscoverServiceApiWorkflowCapabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Input);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateInput(request.Input);
        var descriptor = ResolveExactOperationDescriptor(request.Input);
        if (descriptor is not null)
            return ResolvedOperation(descriptor);

        var managedResult = await managedDiscoveryExecutor.DiscoverAsync(
            new ManagedCodexServiceApiSkillDiscoveryRequest(request.Access, request.Input.Clone()),
            cancellationToken);

        return managedResult.ResultCase switch
        {
            ManagedCodexServiceApiSkillDiscoveryResult.ResultOneofCase.NoReliableApiSkill =>
                NoReliable(managedResult.NoReliableApiSkill),
            ManagedCodexServiceApiSkillDiscoveryResult.ResultOneofCase.ReliableSkill =>
                await ResolveReliableSkillAsync(
                    request,
                    managedResult.ReliableSkill,
                    cancellationToken),
            _ => throw new InvalidOperationException(
                "Managed service API skill discovery returned no typed result."),
        };
    }

    private async Task<ServiceApiWorkflowCapabilityDiscoveryResult> ResolveReliableSkillAsync(
        DiscoverServiceApiWorkflowCapabilityRequest request,
        ReliableServiceApiSkillCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.RequestShape?.Selector is null ||
            candidate.RequestShape.Selector.UserServiceId.Length == 0)
        {
            return NoReliable(ServiceApiNoReliableSkillReason.RequestShapeUnsupported);
        }

        var verification = await exactSkillVerifier.VerifyAsync(
            new ExactServiceApiSkillVerificationRequest(
                request.Access,
                request.Input.Clone(),
                candidate.Clone()),
            cancellationToken);
        if (!verification.IsVerified)
            return NoReliable(verification.Rejection ?? new NoReliableServiceApiSkill
            {
                Reason = ServiceApiNoReliableSkillReason.SkillIntegrityMismatch,
            });

        var readiness = await readinessPort.InspectAsync(
            new InspectExternalWorkflowCapabilityReadinessRequest(
                request.Access,
                new ExternalWorkflowCapabilitySelector
                {
                    NyxIdRequest = candidate.RequestShape.Selector.Clone(),
                },
                ExternalCapabilityExecutionMode.Interactive),
            cancellationToken);
        if (readiness.Status != ExternalCapabilityReadinessStatus.Ready ||
            readiness.SelectedCapability?.CapabilityCase !=
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest ||
            readiness.SelectedCapability.NyxIdUserRequest.Request is null)
        {
            return NoReliable(ServiceApiNoReliableSkillReason.RequestShapeAdmissionRejected);
        }

        return new ServiceApiWorkflowCapabilityDiscoveryResult
        {
            Resolution = new ServiceApiCapabilityResolution
            {
                NyxidRequest = new ResolvedNyxIdRequest
                {
                    OrnnSkill = verification.Provenance!.Clone(),
                    UserServiceId = request.Input.TargetUserServiceId,
                    RequestShape = new AdmittedNyxIdRequestShape
                    {
                        Selector = readiness.SelectedCapability.NyxIdUserRequest.Request.Clone(),
                    },
                    AdmissionPolicyVersion = request.Input.AdmissionPolicyVersion,
                },
            },
        };
    }

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
                    StringComparison.Ordinal))
            .OrderBy(static descriptor => descriptor.Selector.NyxIdOperation.EndpointId, StringComparer.Ordinal)
            .Take(2)
            .ToArray();

        return matches.Length == 1 ? matches[0].Clone() : null;
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

    private static ServiceApiWorkflowCapabilityDiscoveryResult NoReliable(
        ServiceApiNoReliableSkillReason reason) =>
        NoReliable(new NoReliableServiceApiSkill { Reason = reason });

    private static ServiceApiWorkflowCapabilityDiscoveryResult NoReliable(
        NoReliableServiceApiSkill noReliable) =>
        new()
        {
            NoReliableApiSkill = noReliable.Clone(),
        };

    private static void ValidateInput(ServiceApiSkillDiscoveryInput input)
    {
        if (string.IsNullOrWhiteSpace(input.TargetUserServiceId))
            throw new InvalidOperationException("target_user_service_id is required.");
        if (string.IsNullOrWhiteSpace(input.NormalizedCapability))
            throw new InvalidOperationException("normalized_capability is required.");
        if (!CapabilityFingerprintPattern().IsMatch(input.CapabilityFingerprint))
        {
            throw new InvalidOperationException(
                "capability_fingerprint must be 64 lowercase SHA-256 hex characters.");
        }
        if (!string.Equals(
                input.ManagedDiscoveryPolicyVersion,
                "service_api_skill_discovery.v1",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "managed_discovery_policy_version must be service_api_skill_discovery.v1.");
        }
        if (string.IsNullOrWhiteSpace(input.AdmissionPolicyVersion))
            throw new InvalidOperationException("admission_policy_version is required.");
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityFingerprintPattern();
}
