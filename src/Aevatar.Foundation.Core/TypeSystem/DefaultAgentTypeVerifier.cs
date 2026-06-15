using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Foundation.Core.TypeSystem;

/// <summary>
/// Default verifier based on runtime type evidence.
/// </summary>
public sealed class DefaultAgentTypeVerifier : IAgentTypeVerifier
{
    private readonly IActorTypeProbe _typeProbe;

    public DefaultAgentTypeVerifier(IActorTypeProbe typeProbe)
    {
        _typeProbe = typeProbe;
    }

    public async Task<bool> IsExpectedAsync(string actorId, Type expectedType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(expectedType);

        var runtimeTypeName = await _typeProbe.GetRuntimeAgentTypeNameAsync(actorId, ct);
        if (!string.IsNullOrWhiteSpace(runtimeTypeName))
            return AgentTypeNameMatcher.MatchesExpectedType(runtimeTypeName, expectedType);

        return false;
    }
}
