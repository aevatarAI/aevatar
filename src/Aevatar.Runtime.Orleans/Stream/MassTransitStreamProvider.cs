// ─────────────────────────────────────────────────────────────
// MassTransitStreamProvider - IStreamProvider backed by MassTransit.
// Creates or reuses MassTransitStream instances per actor ID.
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using MassTransit;

namespace Aevatar.Orleans.Stream;

/// <summary>
/// MassTransit-backed stream provider. One stream per actor ID.
/// </summary>
public sealed class MassTransitStreamProvider : IStreamProvider
{
    private readonly ConcurrentDictionary<string, MassTransitStream> _streams = new();
    private readonly ISendEndpointProvider _sendEndpointProvider;
    private readonly IBus _bus;

    /// <summary>Creates a MassTransit stream provider.</summary>
    public MassTransitStreamProvider(ISendEndpointProvider sendEndpointProvider, IBus bus)
    {
        _sendEndpointProvider = sendEndpointProvider;
        _bus = bus;
    }

    /// <inheritdoc />
    public IStream GetStream(string actorId) =>
        _streams.GetOrAdd(actorId, id =>
            new MassTransitStream(id, _sendEndpointProvider, _bus));
}
