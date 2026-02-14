// ─────────────────────────────────────────────────────────────
// OrleansClientActor - IActor proxy (works from Client or Silo).
// All methods delegate to Grain RPC or MassTransit Stream.
// No in-process IAgent reference — fully distribution-safe.
// ─────────────────────────────────────────────────────────────

using Aevatar.Orleans.Grains;

namespace Aevatar.Orleans.Actors;

/// <summary>
/// Actor proxy. Events are sent via MassTransit Stream (fire-and-forget).
/// Metadata queries use Grain RPC ([AlwaysInterleave]).
/// </summary>
public sealed class OrleansClientActor : IActor
{
    private readonly IGAgentGrain _grain;
    private readonly IStream _stream;

    /// <summary>Creates an actor proxy.</summary>
    public OrleansClientActor(string id, IGAgentGrain grain, IStream stream)
    {
        Id = id;
        _grain = grain;
        _stream = stream;
    }

    /// <inheritdoc />
    public string Id { get; }

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
    }

    /// <summary>Gets parent actor ID via Grain RPC.</summary>
    public Task<string?> GetParentIdAsync() => _grain.GetParentAsync();

    /// <summary>Gets child actor IDs via Grain RPC.</summary>
    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => _grain.GetChildrenAsync();

    /// <summary>Gets agent description via Grain RPC.</summary>
    public Task<string> GetDescriptionAsync() => _grain.GetDescriptionAsync();

    /// <summary>Gets agent type name via Grain RPC.</summary>
    public Task<string> GetAgentTypeNameAsync() => _grain.GetAgentTypeNameAsync();

    /// <summary>Sends JSON config to agent via Grain RPC.</summary>
    public Task ConfigureAsync(string configJson, CancellationToken ct = default) =>
        _grain.ConfigureAsync(configJson);
}
