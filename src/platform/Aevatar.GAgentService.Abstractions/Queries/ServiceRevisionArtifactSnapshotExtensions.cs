using Aevatar.GAgentService.Abstractions.Services;

namespace Aevatar.GAgentService.Abstractions.Queries;

public static class ServiceRevisionArtifactSnapshotExtensions
{
    // Refactor (iter100/cluster-100): Old artifact lookup used a process-local store keyed by service/revision. / New callers resolve it from the revision readmodel.
    public static PreparedServiceRevisionArtifact GetRequiredPreparedArtifact(
        this ServiceRevisionCatalogSnapshot? catalog,
        ServiceIdentity identity,
        string revisionId)
    {
        return catalog.TryGetPreparedArtifact(revisionId, out var artifact)
            ? artifact
            : throw new InvalidOperationException(BuildMissingArtifactMessage(catalog, identity, revisionId));
    }

    // The activation handler reads the prepared artifact from the projected revision readmodel, which
    // lags behind the just-committed prepare event. A null catalog / absent revision / absent prepared
    // artifact therefore means "not materialized yet" rather than a terminal failure; the caller re-arms
    // a bounded self-continuation instead of throwing. PreparationFailed is reported separately so the
    // caller can fail fast on a genuine terminal revision.
    public static bool TryGetPreparedArtifact(
        this ServiceRevisionCatalogSnapshot? catalog,
        string revisionId,
        out PreparedServiceRevisionArtifact artifact)
    {
        artifact = null!;
        var revision = catalog?.Revisions.FirstOrDefault(x =>
            string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal));
        if (revision?.PreparedArtifact == null ||
            string.IsNullOrWhiteSpace(revision.PreparedArtifact.RevisionId))
        {
            return false;
        }

        artifact = revision.PreparedArtifact.Clone();
        return true;
    }

    public static bool TryGetPublishedPreparedArtifact(
        this ServiceRevisionCatalogSnapshot? catalog,
        string revisionId,
        string? expectedArtifactHash,
        out PreparedServiceRevisionArtifact artifact)
    {
        artifact = null!;
        if (catalog == null)
            return false;

        var revision = catalog.Revisions.FirstOrDefault(x =>
            string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal));
        if (revision == null ||
            !string.Equals(
                revision.Status,
                ServiceRevisionStatus.Published.ToString(),
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(revision.ArtifactHash) ||
            revision.PreparedArtifact == null ||
            revision.PreparedArtifact.ImplementationKind == ServiceImplementationKind.Unspecified ||
            !string.Equals(
                revision.ImplementationKind,
                revision.PreparedArtifact.ImplementationKind.ToString(),
                StringComparison.OrdinalIgnoreCase) ||
            !MatchesCatalogIdentity(catalog.ServiceKey, revision.PreparedArtifact.Identity) ||
            !string.Equals(revision.PreparedArtifact.RevisionId, revisionId, StringComparison.Ordinal) ||
            !string.Equals(
                revision.ArtifactHash ?? string.Empty,
                revision.PreparedArtifact.ArtifactHash ?? string.Empty,
                StringComparison.Ordinal) ||
            !WorkflowServiceRevisionEquivalence.HasValidArtifactHash(revision.PreparedArtifact))
        {
            return false;
        }

        var normalizedExpectedArtifactHash = expectedArtifactHash?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(normalizedExpectedArtifactHash) &&
            !string.Equals(
                normalizedExpectedArtifactHash,
                revision.PreparedArtifact.ArtifactHash,
                StringComparison.Ordinal))
        {
            return false;
        }

        artifact = revision.PreparedArtifact.Clone();
        return true;
    }

    private static bool MatchesCatalogIdentity(string serviceKey, ServiceIdentity? identity) =>
        identity != null &&
        !string.IsNullOrWhiteSpace(identity.TenantId) &&
        !string.IsNullOrWhiteSpace(identity.AppId) &&
        !string.IsNullOrWhiteSpace(identity.Namespace) &&
        !string.IsNullOrWhiteSpace(identity.ServiceId) &&
        string.Equals(serviceKey, ServiceKeys.Build(identity), StringComparison.Ordinal);

    public static bool IsRevisionPreparationFailed(
        this ServiceRevisionCatalogSnapshot? catalog,
        string revisionId)
    {
        var revision = catalog?.Revisions.FirstOrDefault(x =>
            string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal));
        return revision != null &&
               string.Equals(
                   revision.Status,
                   ServiceRevisionStatus.PreparationFailed.ToString(),
                   StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRevisionPublished(
        this ServiceRevisionCatalogSnapshot? catalog,
        string revisionId)
    {
        var revision = catalog?.Revisions.FirstOrDefault(x =>
            string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal));
        return revision != null &&
               string.Equals(
                   revision.Status,
                   ServiceRevisionStatus.Published.ToString(),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildMissingArtifactMessage(
        ServiceRevisionCatalogSnapshot? catalog,
        ServiceIdentity identity,
        string revisionId)
    {
        var serviceKey = ServiceKeys.Build(identity);
        return catalog == null
            ? $"Revision catalog for '{serviceKey}' was not found."
            : $"Prepared artifact for '{serviceKey}' revision '{revisionId}' was not found.";
    }
}
