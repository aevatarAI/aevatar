using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.Foundation.Core.TypeSystem;

/// <summary>
/// Default verifier based on actor-owned runtime kind evidence.
/// </summary>
public sealed class DefaultAgentKindVerifier : IAgentKindVerifier
{
    private readonly IActorKindProbe _kindProbe;

    public DefaultAgentKindVerifier(IActorKindProbe kindProbe)
    {
        _kindProbe = kindProbe ?? throw new ArgumentNullException(nameof(kindProbe));
    }

    public async Task<bool> IsExpectedKindAsync(string actorId, string expectedKind, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKind);

        var runtimeKind = await _kindProbe.GetRuntimeAgentKindAsync(actorId, ct);
        return string.Equals(runtimeKind, expectedKind.Trim(), StringComparison.Ordinal);
    }
}
