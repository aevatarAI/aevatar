using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultBuildApprovalPort : IBuildApprovalPort
{
    public Task<BuildApprovalDecision> DecideAsync(BuildApprovalRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.SourceBundleDigest))
            return Task.FromResult(new BuildApprovalDecision(false, false, "BUILD_POLICY_REJECTED"));

        return Task.FromResult(new BuildApprovalDecision(true, false));
    }
}
