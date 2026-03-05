using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Foundation.Runtime.Implementations.Local.TypeSystem;

/// <summary>
/// Runtime type probe for local in-process actors.
/// </summary>
public sealed class LocalActorTypeProbe : IActorTypeProbe
{
    private readonly IActorRuntime _runtime;

    public LocalActorTypeProbe(IActorRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task<string?> GetRuntimeAgentTypeNameAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var actor = await _runtime.GetAsync(actorId);
        var runtimeType = actor?.Agent?.GetType();
        if (runtimeType == null)
            return null;

        return runtimeType.AssemblyQualifiedName ?? runtimeType.FullName;
    }
}
