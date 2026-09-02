using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IContentArtifactQueryPort
{
    Task<ContentArtifactListResponse> ListAsync(
        string scopeId,
        string requesterPrincipalId,
        ContentArtifactQueryRequest query,
        CancellationToken ct = default);

    Task<ContentArtifactCurrentStateResponse?> GetAsync(
        string scopeId,
        string artifactId,
        CancellationToken ct = default);

    Task<ContentArtifactCurrentStateResponse?> GetByDedupKeyAsync(
        string scopeId,
        string dedupKey,
        CancellationToken ct = default);

    Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(
        string scopeId,
        string artifactId,
        string revisionId,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default);
}
