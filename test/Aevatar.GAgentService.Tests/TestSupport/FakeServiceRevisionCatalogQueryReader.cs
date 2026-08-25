using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;

namespace Aevatar.GAgentService.Tests.TestSupport;

internal sealed class FakeServiceRevisionCatalogQueryReader : IServiceRevisionCatalogQueryReader
{
    private readonly Dictionary<string, PreparedServiceRevisionArtifact> _revisionCatalog = new(StringComparer.Ordinal);

    public Task UpsertRevisionAsync(
        string serviceKey,
        string revisionId,
        PreparedServiceRevisionArtifact artifact,
        CancellationToken ct = default)
    {
        var clone = artifact.Clone();
        clone.RevisionId = revisionId;
        _revisionCatalog[BuildKey(serviceKey, revisionId)] = clone;
        return Task.CompletedTask;
    }

    public Task<ServiceRevisionCatalogSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default)
    {
        var serviceKey = ServiceKeys.Build(identity);
        var revisions = _revisionCatalog
            .Where(x => x.Key.StartsWith(serviceKey + ":", StringComparison.Ordinal))
            .Select(x => x.Value)
            .OrderBy(x => x.RevisionId, StringComparer.Ordinal)
            .Select(artifact => new ServiceRevisionSnapshot(
                artifact.RevisionId,
                artifact.ImplementationKind.ToString(),
                ServiceRevisionStatus.Published.ToString(),
                artifact.ArtifactHash,
                string.Empty,
                artifact.Endpoints.Select(endpoint => new ServiceEndpointSnapshot(
                    endpoint.EndpointId,
                    endpoint.DisplayName,
                    endpoint.Kind.ToString(),
                    endpoint.RequestTypeUrl,
                    endpoint.ResponseTypeUrl,
                    endpoint.Description)).ToList(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                null,
                artifact.Clone()))
            .ToList();

        return Task.FromResult<ServiceRevisionCatalogSnapshot?>(new ServiceRevisionCatalogSnapshot(
            serviceKey,
            revisions,
            DateTimeOffset.UtcNow,
            revisions.Count,
            revisions.Count == 0 ? string.Empty : $"{serviceKey}:test-revisions"));
    }

    private static string BuildKey(string serviceKey, string revisionId) => $"{serviceKey}:{revisionId}";
}
