using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IContentArtifactCommandPort
{
    Task<ContentArtifactAcceptedReceipt> CreateAsync(
        string scopeId,
        CreateContentArtifactRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default);

    Task<ContentArtifactAcceptedReceipt> AppendRevisionAsync(
        string scopeId,
        string artifactId,
        AppendContentArtifactRevisionRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default);

    Task<ContentArtifactAcceptedReceipt> AdvanceCurrentRevisionAsync(
        string scopeId,
        string artifactId,
        AdvanceContentArtifactCurrentRevisionRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default);

    Task<ContentArtifactAcceptedReceipt> RedactRevisionAsync(
        string scopeId,
        string artifactId,
        string revisionId,
        RedactContentArtifactRevisionRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default);

    Task<ContentArtifactAcceptedReceipt> ExpireRevisionAsync(
        string scopeId,
        string artifactId,
        string revisionId,
        ExpireContentArtifactRevisionRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default);

    Task<ContentArtifactAcceptedReceipt> TombstoneAsync(
        string scopeId,
        string artifactId,
        TombstoneContentArtifactRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default);
}

public interface IContentArtifactPinCommandPort
{
    Task<ContentArtifactPinAcceptedReceipt> SetAsync(
        string scopeId,
        string pinKey,
        SetContentArtifactPinRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default);

    Task<ContentArtifactPinAcceptedReceipt> ClearAsync(
        string scopeId,
        string pinKey,
        ClearContentArtifactPinRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default);
}
