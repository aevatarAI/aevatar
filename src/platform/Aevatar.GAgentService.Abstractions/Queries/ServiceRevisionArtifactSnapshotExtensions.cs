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
