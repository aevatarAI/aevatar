using Aevatar.DynamicRuntime.Abstractions.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultImageReferenceResolver : IImageReferenceResolver
{
    public Task<ImageDigestResolveResult> ResolveAsync(string imageName, string tagOrDigest, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(tagOrDigest))
            return Task.FromResult(new ImageDigestResolveResult(false, string.Empty, "IMAGE_NOT_PUBLISHED"));

        if (tagOrDigest.StartsWith("sha256:", StringComparison.Ordinal))
            return Task.FromResult(new ImageDigestResolveResult(true, tagOrDigest));

        var normalized = $"{imageName}:{tagOrDigest}";
        var digest = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()}";
        return Task.FromResult(new ImageDigestResolveResult(true, digest));
    }
}
