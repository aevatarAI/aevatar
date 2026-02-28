using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class DefaultImageReferenceResolver : IImageReferenceResolver
{
    public Task<string> ResolveDigestAsync(string imageName, string tagOrDigest, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(tagOrDigest))
            throw new InvalidOperationException("IMAGE_NOT_PUBLISHED");

        if (tagOrDigest.StartsWith("sha256:", StringComparison.Ordinal))
            return Task.FromResult(tagOrDigest);

        return Task.FromResult($"sha256:{imageName}:{tagOrDigest}");
    }
}
