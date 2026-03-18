using System.Collections.Immutable;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;

namespace Aevatar.GAgentService.Infrastructure.Artifacts;

/// <summary>
/// In-process artifact store backed by an immutable dictionary with atomic swap.
/// <para>
/// Artifacts are saved during revision preparation and read at invocation-time.
/// This implementation is suitable for single-node deployments. In a multi-node
/// cluster, replace with a distributed or persistent store registered against
/// <see cref="IServiceRevisionArtifactStore"/> so that artifacts survive restarts
/// and remain visible across all nodes.
/// </para>
/// </summary>
public sealed class ConfiguredServiceRevisionArtifactStore : IServiceRevisionArtifactStore
{
    private ImmutableDictionary<string, PreparedServiceRevisionArtifact> _artifacts =
        ImmutableDictionary<string, PreparedServiceRevisionArtifact>.Empty.WithComparers(StringComparer.Ordinal);

    public Task SaveAsync(
        string serviceKey,
        string revisionId,
        PreparedServiceRevisionArtifact artifact,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = BuildKey(serviceKey, revisionId);
        ImmutableInterlocked.AddOrUpdate(ref _artifacts, key, artifact.Clone(), (_, _) => artifact.Clone());
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
