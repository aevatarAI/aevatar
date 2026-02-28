using System.Security.Cryptography;
using System.Text;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultAgentBuildPlanPort : IAgentBuildPlanPort
{
    public Task<BuildPlanResult> PlanAsync(BuildPlanRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.BuildJobId) ||
            string.IsNullOrWhiteSpace(request.StackId) ||
            string.IsNullOrWhiteSpace(request.ServiceName) ||
            string.IsNullOrWhiteSpace(request.SourceBundleDigest))
            return Task.FromResult(new BuildPlanResult(false, string.Empty, "BUILD_POLICY_REJECTED", "invalid build plan request"));

        var normalized = $"{request.BuildJobId}:{request.StackId}:{request.ServiceName}:{request.SourceBundleDigest}";
        var digest = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()}";
        return Task.FromResult(new BuildPlanResult(true, digest));
    }
}
