using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

/// <summary>
/// Runtime kind probe backed by Orleans grain state.
/// </summary>
public sealed class OrleansActorKindProbe : IActorKindProbe
{
    private readonly IGrainFactory _grainFactory;

    public OrleansActorKindProbe(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    public async Task<string?> GetRuntimeAgentKindAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
        var kind = await grain.GetAgentKindAsync();
        return string.IsNullOrWhiteSpace(kind) ? null : kind;
    }
}
