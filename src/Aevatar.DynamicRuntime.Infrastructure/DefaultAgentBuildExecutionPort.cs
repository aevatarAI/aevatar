using System.Security.Cryptography;
using System.Text;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultAgentBuildExecutionPort : IAgentBuildExecutionPort
{
    public Task<BuildExecutionResult> ExecuteAsync(BuildExecutionRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.BuildPlanDigest) ||
            string.IsNullOrWhiteSpace(request.SourceBundleDigest))
            return Task.FromResult(new BuildExecutionResult(false, string.Empty, "BUILD_POLICY_REJECTED", "build input is invalid"));

        var normalized = $"{request.ImageName}:{request.SourceBundleDigest}:{request.BuildPlanDigest}";
        var digest = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()}";
        return Task.FromResult(new BuildExecutionResult(true, digest));
    }
}
