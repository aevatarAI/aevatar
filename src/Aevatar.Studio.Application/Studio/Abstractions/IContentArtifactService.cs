using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IContentArtifactService
{
    Task<ContentArtifactAcceptedReceipt> CreateAsync(string scopeId, CreateContentArtifactRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
    Task<ContentArtifactListResponse> ListAsync(string scopeId, ContentArtifactQueryRequest query, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
    Task<ContentArtifactCurrentStateResponse> GetAsync(string scopeId, string artifactId, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
    Task<ContentArtifactRevisionResponse> GetRevisionAsync(string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
    Task<ContentArtifactRevisionResponse> GetCurrentRevisionAsync(string scopeId, string artifactId, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
    Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
    Task<ContentArtifactAcceptedReceipt> AppendRevisionAsync(string scopeId, string artifactId, AppendContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
    Task<ContentArtifactAcceptedReceipt> AdvanceCurrentRevisionAsync(string scopeId, string artifactId, AdvanceContentArtifactCurrentRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
    Task<ContentArtifactAcceptedReceipt> RedactRevisionAsync(string scopeId, string artifactId, string revisionId, RedactContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
    Task<ContentArtifactAcceptedReceipt> ExpireRevisionAsync(string scopeId, string artifactId, string revisionId, ExpireContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
    Task<ContentArtifactAcceptedReceipt> TombstoneAsync(string scopeId, string artifactId, TombstoneContentArtifactRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
    Task<ContentArtifactRunAttachmentReceipt> AttachToRunAsync(string scopeId, AttachContentArtifactsToRunRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
}

public interface IContentArtifactPinService
{
    Task<ContentArtifactPinCurrentStateResponse> GetAsync(string scopeId, string pinKey, CancellationToken ct = default);
    Task<ContentArtifactPinAcceptedReceipt> SetAsync(string scopeId, string pinKey, SetContentArtifactPinRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
    Task<ContentArtifactPinAcceptedReceipt> ClearAsync(string scopeId, string pinKey, ClearContentArtifactPinRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default);
}
