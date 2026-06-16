using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Local.ActivationIndex;

namespace Aevatar.Foundation.Runtime.Implementations.Local.TypeSystem;

/// <summary>
/// Runtime kind probe for local in-process actors.
/// </summary>
internal sealed class LocalActorKindProbe : IActorKindProbe
{
    private readonly ILocalActivationIndexStore _activationIndexStore;

    public LocalActorKindProbe(ILocalActivationIndexStore activationIndexStore)
    {
        _activationIndexStore = activationIndexStore ?? throw new ArgumentNullException(nameof(activationIndexStore));
    }

    public async Task<string?> GetRuntimeAgentKindAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var kind = await _activationIndexStore.GetAgentKindAsync(actorId, ct);
        return string.IsNullOrWhiteSpace(kind) ? null : kind;
    }
}
