using System.Collections.Concurrent;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Infrastructure.Artifacts;

public sealed class InMemoryServiceRevisionArtifactStore : IServiceRevisionArtifactStore
{
    private readonly ConcurrentDictionary<string, PreparedServiceRevisionArtifact> _artifacts = new(StringComparer.Ordinal);

    public Task SaveAsync(
        string serviceKey,
        string revisionId,
        PreparedServiceRevisionArtifact artifact,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = BuildKey(serviceKey, revisionId);
        _artifacts[key] = artifact.Clone();
        return Task.CompletedTask;
    }

    public Task<PreparedServiceRevisionArtifact?> GetAsync(
        string serviceKey,
        string revisionId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = BuildKey(serviceKey, revisionId);
        return Task.FromResult(
            _artifacts.TryGetValue(key, out var artifact)
                ? artifact.Clone()
                : null);
    }

    private static string BuildKey(string serviceKey, string revisionId) =>
        $"{serviceKey}:{revisionId}";
}
