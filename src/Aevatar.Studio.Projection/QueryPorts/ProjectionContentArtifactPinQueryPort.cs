using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.QueryPorts;

public sealed class ProjectionContentArtifactPinQueryPort : IContentArtifactPinQueryPort
{
    private readonly IProjectionDocumentReader<ContentArtifactPinCurrentStateDocument, string> _documentReader;

    public ProjectionContentArtifactPinQueryPort(
        IProjectionDocumentReader<ContentArtifactPinCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<ContentArtifactPinCurrentStateResponse?> GetAsync(
        string scopeId,
        string pinKey,
        CancellationToken ct = default)
    {
        var normalizedScopeId = ContentArtifactConventions.NormalizeScopeId(scopeId);
        var normalizedPinKey = ContentArtifactConventions.NormalizeLabelKey(pinKey, nameof(pinKey));
        var document = await _documentReader.GetAsync(
            ContentArtifactConventions.BuildPinActorId(normalizedScopeId, normalizedPinKey),
            ct);
        if (document == null ||
            !string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal) ||
            !string.Equals(document.PinKey, normalizedPinKey, StringComparison.Ordinal))
        {
            return null;
        }

        return new ContentArtifactPinCurrentStateResponse(
            document.ScopeId,
            document.PinKey,
            NormalizeOptional(document.PinnedArtifactId),
            string.IsNullOrWhiteSpace(document.PinnedByPrincipalId)
                ? null
                : new ContentArtifactPrincipalContract(
                    document.PinnedByPrincipalId,
                    document.PinnedByPrincipalKind),
            document.PinVersion,
            document.StateVersion,
            document.PinUpdatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            document.LastMutationId,
            document.LastMutationStatus,
            NormalizeOptional(document.LastRejectionCode),
            string.IsNullOrWhiteSpace(document.LastMutationRequestedByPrincipalId)
                ? null
                : new ContentArtifactPrincipalContract(
                    document.LastMutationRequestedByPrincipalId,
                    document.LastMutationRequestedByPrincipalKind));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
