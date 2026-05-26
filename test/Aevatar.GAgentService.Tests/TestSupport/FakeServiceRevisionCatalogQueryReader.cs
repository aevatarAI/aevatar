using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;

namespace Aevatar.GAgentService.Tests.TestSupport;

internal sealed class FakeServiceRevisionCatalogQueryReader : IServiceRevisionCatalogQueryReader
{
    private readonly Dictionary<string, PreparedServiceRevisionArtifact> _artifacts = new(StringComparer.Ordinal);

    public Task SaveAsync(
        string serviceKey,
        string revisionId,
        PreparedServiceRevisionArtifact artifact,
        CancellationToken ct = default)
    {
        var clone = artifact.Clone();
        clone.RevisionId = revisionId;
        _artifacts[BuildKey(serviceKey, revisionId)] = clone;
        return Task.CompletedTask;
    }

    public Task<PreparedServiceRevisionArtifact?> GetAsync(
        string serviceKey,
        string revisionId,
        CancellationToken ct = default)
    {
        _artifacts.TryGetValue(BuildKey(serviceKey, revisionId), out var artifact);
        return Task.FromResult(artifact?.Clone());
    }

    public Task<ServiceRevisionCatalogSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default)
    {
        var serviceKey = ServiceKeys.Build(identity);
        var revisions = _artifacts
            .Where(x => x.Key.StartsWith(serviceKey + ":", StringComparison.Ordinal))
            .Select(x => x.Value)
            .OrderBy(x => x.RevisionId, StringComparer.Ordinal)
            .Select(artifact => new ServiceRevisionSnapshot(
                artifact.RevisionId,
                artifact.ImplementationKind.ToString(),
                ServiceRevisionStatus.Prepared.ToString(),
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
                null,
                null,
                null,
                artifact.Clone()))
            .ToList();

        return Task.FromResult<ServiceRevisionCatalogSnapshot?>(new ServiceRevisionCatalogSnapshot(
            serviceKey,
            revisions,
            DateTimeOffset.UtcNow,
            revisions.Count,
            revisions.Count == 0 ? string.Empty : $"{serviceKey}:test-artifacts"));
    }

    private static string BuildKey(string serviceKey, string revisionId) => $"{serviceKey}:{revisionId}";
}
