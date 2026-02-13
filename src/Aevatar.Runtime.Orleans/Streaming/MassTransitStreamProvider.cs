// ─────────────────────────────────────────────────────────────
// MassTransitStreamProvider - IStreamProvider backed by MassTransit.
// Creates or reuses MassTransitStream instances per actor ID.
// ─────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Aevatar.Orleans.Consumers;
using MassTransit;

namespace Aevatar.Orleans.Streaming;

/// <summary>
/// MassTransit-backed stream provider. One stream per actor ID.
/// Uses <see cref="IAgentEventSender"/> for transport-agnostic producing
/// and <see cref="IBus"/> for dynamic subscription endpoints.
/// </summary>
public sealed class MassTransitStreamProvider : IStreamProvider
{
    private readonly ConcurrentDictionary<string, MassTransitStream> _streams = new();
    private readonly IAgentEventSender _sender;
    private readonly IBus _bus;

    /// <summary>Creates a MassTransit stream provider.</summary>
    public MassTransitStreamProvider(IAgentEventSender sender, IBus bus)
    {
        _sender = sender;
        _bus = bus;
    }

    /// <inheritdoc />
    public IStream GetStream(string actorId) =>
        _streams.GetOrAdd(actorId, id =>
            new MassTransitStream(id, _sender, _bus));
}
