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
        var serviceKey = ServiceKeys.Build(identity);
        if (catalog == null)
        {
            throw new InvalidOperationException(
                $"Revision catalog for '{serviceKey}' was not found.");
        }

        var revision = catalog.Revisions.FirstOrDefault(x =>
            string.Equals(x.RevisionId, revisionId, StringComparison.Ordinal));
        if (revision == null)
        {
            throw new InvalidOperationException(
                $"Prepared artifact for '{serviceKey}' revision '{revisionId}' was not found.");
        }

        if (revision.PreparedArtifact == null ||
            string.IsNullOrWhiteSpace(revision.PreparedArtifact.RevisionId))
        {
            throw new InvalidOperationException(
                $"Prepared artifact for '{serviceKey}' revision '{revisionId}' was not found.");
        }

        return revision.PreparedArtifact.Clone();
    }
}
