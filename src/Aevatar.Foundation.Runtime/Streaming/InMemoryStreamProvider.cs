// ─────────────────────────────────────────────────────────────
// InMemoryStreamProvider - in-memory stream provider.
// Creates or resolves InMemoryStream by actor ID and supports stream removal.
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Streaming;

/// <summary>In-memory stream provider maintaining one event stream per actor.</summary>
public sealed class InMemoryStreamProvider : IStreamProvider
{
    private readonly ConcurrentDictionary<string, InMemoryStream> _streams = new();
    private readonly InMemoryStreamOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public InMemoryStreamProvider()
        : this(new InMemoryStreamOptions(), NullLoggerFactory.Instance)
    {
    }

    public InMemoryStreamProvider(
        InMemoryStreamOptions options,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Gets or creates stream for specified actor.</summary>
    /// <param name="actorId">Unique actor identifier.</param>
    /// <returns>Actor event stream instance.</returns>
    public IStream GetStream(string actorId) =>
        _streams.GetOrAdd(actorId, id =>
            new InMemoryStream(
                id,
                _options,
                _loggerFactory.CreateLogger<InMemoryStream>()));

    /// <summary>Removes and shuts down stream for specified actor.</summary>
    /// <param name="actorId">Actor ID to remove.</param>
    public void RemoveStream(string actorId)
    {
        if (_streams.TryRemove(actorId, out var stream)) stream.Shutdown();
    }
}
