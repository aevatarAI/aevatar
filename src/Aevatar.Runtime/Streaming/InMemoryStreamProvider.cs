// ─────────────────────────────────────────────────────────────
// InMemoryStreamProvider - in-memory stream provider.
// Creates or resolves InMemoryStream by actor ID and supports stream removal.
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;

namespace Aevatar.Streaming;

/// <summary>In-memory stream provider maintaining one event stream per actor.</summary>
public sealed class InMemoryStreamProvider : IStreamProvider
{
    private readonly ConcurrentDictionary<string, InMemoryStream> _streams = new();

    /// <summary>Gets or creates stream for specified actor.</summary>
    /// <param name="actorId">Unique actor identifier.</param>
    /// <returns>Actor event stream instance.</returns>
    public IStream GetStream(string actorId) =>
        _streams.GetOrAdd(actorId, id => new InMemoryStream(id));

    /// <summary>Removes and shuts down stream for specified actor.</summary>
    /// <param name="actorId">Actor ID to remove.</param>
    public void RemoveStream(string actorId)
    {
        if (_streams.TryRemove(actorId, out var stream)) stream.Shutdown();
    }
}
