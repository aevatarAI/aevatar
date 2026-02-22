using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Foundation.Core.TypeSystem;

/// <summary>
/// Default verifier that prefers runtime type evidence, then falls back to manifest.
/// </summary>
public sealed class DefaultAgentTypeVerifier : IAgentTypeVerifier
{
    private readonly IActorTypeProbe _typeProbe;
    private readonly IAgentManifestStore _manifestStore;

    public DefaultAgentTypeVerifier(IActorTypeProbe typeProbe, IAgentManifestStore manifestStore)
    {
        _typeProbe = typeProbe;
        _manifestStore = manifestStore;
    }

    public async Task<bool> IsExpectedAsync(string actorId, Type expectedType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(expectedType);

        var runtimeTypeName = await _typeProbe.GetRuntimeAgentTypeNameAsync(actorId, ct);
        if (!string.IsNullOrWhiteSpace(runtimeTypeName))
            return AgentTypeNameMatcher.MatchesExpectedType(runtimeTypeName, expectedType);

        var manifest = await _manifestStore.LoadAsync(actorId, ct);
        return AgentTypeNameMatcher.MatchesExpectedType(manifest?.AgentTypeName, expectedType);
    }
}
