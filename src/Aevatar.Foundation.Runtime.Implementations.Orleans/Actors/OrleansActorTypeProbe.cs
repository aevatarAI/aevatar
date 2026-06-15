using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

/// <summary>
/// Runtime type probe backed by Orleans grain state.
/// </summary>
public sealed class OrleansActorTypeProbe : IActorTypeProbe
{
    private readonly IGrainFactory _grainFactory;

    public OrleansActorTypeProbe(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    public async Task<string?> GetRuntimeAgentTypeNameAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(actorId);
        var typeName = await grain.GetAgentTypeNameAsync();
        return string.IsNullOrWhiteSpace(typeName) ? null : typeName;
    }
}
