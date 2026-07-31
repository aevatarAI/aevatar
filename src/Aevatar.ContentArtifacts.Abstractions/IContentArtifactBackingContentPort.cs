namespace Aevatar.ContentArtifacts.Abstractions;

public sealed record ContentArtifactBackingContentDescriptor(
    long ByteLength,
    string ContentHash);

public sealed record ContentArtifactBackingContentRequest(
    ContentArtifactBackingObjectReference Reference,
    string ScopeId,
    string? RunId = null);

public interface IContentArtifactBackingContentPort
{
    Task<ContentArtifactBackingContentDescriptor> DescribeAsync(
        ContentArtifactBackingContentRequest request,
        CancellationToken ct = default);

    Task<Stream> OpenReadAsync(
        ContentArtifactBackingContentRequest request,
        CancellationToken ct = default);
}
