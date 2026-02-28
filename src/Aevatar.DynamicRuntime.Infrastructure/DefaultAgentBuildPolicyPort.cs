using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultAgentBuildPolicyPort : IAgentBuildPolicyPort
{
    public Task<BuildPolicyDecision> ValidateAsync(BuildPolicyRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.SourceBundleDigest))
            return Task.FromResult(new BuildPolicyDecision(false, false, "Rejected", "BUILD_POLICY_REJECTED", "source bundle digest is required"));
        if (string.IsNullOrWhiteSpace(request.BuildPlanDigest))
            return Task.FromResult(new BuildPolicyDecision(false, false, "Rejected", "BUILD_POLICY_REJECTED", "build plan digest is required"));

        var requiresManual = request.SourceBundleDigest.Contains("manual", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new BuildPolicyDecision(true, requiresManual, requiresManual ? "ManualApprovalRequired" : "AutoApproved"));
    }
}
