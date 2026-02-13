// ─────────────────────────────────────────────────────────────
// OrleansClientActor - Client-side IActor proxy.
// HandleEventAsync sends to MassTransit Stream (fire-and-forget).
// Hierarchy queries use Grain RPC ([AlwaysInterleave]).
// Results arrive via IStreamProvider subscription on the Client.
// ─────────────────────────────────────────────────────────────

using Aevatar.Orleans.Grain;

namespace Aevatar.Orleans.Actor;

/// <summary>
/// Client-side actor proxy. Events are sent via MassTransit Stream
/// (fire-and-forget). Results are received through stream subscription.
/// </summary>
public sealed class OrleansClientActor : IActor
{
    private readonly IGAgentGrain _grain;
    private readonly IStream _stream;

    /// <summary>Creates a client-side actor proxy.</summary>
    /// <param name="id">Actor ID.</param>
    /// <param name="grain">Orleans Grain reference.</param>
    /// <param name="stream">MassTransit stream for this actor.</param>
    public OrleansClientActor(string id, IGAgentGrain grain, IStream stream)
    {
        Id = id;
        _grain = grain;
        _stream = stream;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>
    /// Agent runs inside the Grain (Silo). Direct access is not supported.
    /// Subscribe to the stream for output events instead.
    /// </summary>
    public IAgent Agent => throw new NotSupportedException(
        "Agent runs inside Grain (Silo). " +
        "Use IStreamProvider.GetStream(Id).SubscribeAsync(...) for output events.");

    /// <summary>Grain is already initialized during CreateAsync; no-op.</summary>
    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Requests Grain deactivation.</summary>
    public Task DeactivateAsync(CancellationToken ct = default) =>
        _grain.DeactivateAsync();

    /// <summary>
    /// Sends event to MassTransit Stream (fire-and-forget).
    /// MassTransitEventHandler consumes the message and routes it to the Grain.
    /// </summary>
    public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        await _stream.ProduceAsync(envelope, ct);
        // Fire-and-forget: returns as soon as the message is enqueued.
        // The Grain consumes from MassTransit at its own pace.
    }

    /// <summary>Gets parent actor ID via Grain RPC (read-only, [AlwaysInterleave]).</summary>
    public Task<string?> GetParentIdAsync() => _grain.GetParentAsync();

    /// <summary>Gets child actor IDs via Grain RPC (read-only, [AlwaysInterleave]).</summary>
    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => _grain.GetChildrenAsync();
}
