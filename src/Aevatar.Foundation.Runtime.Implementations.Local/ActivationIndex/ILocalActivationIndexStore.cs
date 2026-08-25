using Aevatar.Foundation.Abstractions.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Local.ActivationIndex;

internal interface ILocalActivationIndexStore
{
    Task UpsertAsync(string actorId, RuntimeActorIdentity identity, CancellationToken ct = default);

    Task<RuntimeActorIdentity?> GetIdentityAsync(string actorId, CancellationToken ct = default);

    Task<string?> GetAgentKindAsync(string actorId, CancellationToken ct = default);

    Task DeleteAsync(string actorId, CancellationToken ct = default);
}

internal sealed class InMemoryLocalActivationIndexStore : ILocalActivationIndexStore
{
    private readonly ILocalActorRuntimeEnvelopeStore _envelopes;

    public InMemoryLocalActivationIndexStore(ILocalActorRuntimeEnvelopeStore envelopes)
    {
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
    }

    public Task UpsertAsync(
        string actorId,
        RuntimeActorIdentity identity,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Kind);
        ct.ThrowIfCancellationRequested();
        return UpsertCoreAsync(actorId, identity, ct);
    }

    public Task<RuntimeActorIdentity?> GetIdentityAsync(
        string actorId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return GetIdentityCoreAsync(actorId, ct);
    }

    public Task<string?> GetAgentKindAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return GetAgentKindCoreAsync(actorId, ct);
    }

    public Task DeleteAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        return _envelopes.DeleteAsync(actorId, ct);
    }

    private async Task UpsertCoreAsync(
        string actorId,
        RuntimeActorIdentity identity,
        CancellationToken ct)
    {
        while (true)
        {
            var current = await _envelopes.GetAsync(actorId, ct);
            var next = current?.Clone() ?? new RuntimeActorStateEnvelope();
            next.Identity = identity.Clone();
            if (await _envelopes.CompareExchangeAsync(actorId, current, next, ct))
                return;
        }
    }

    private async Task<RuntimeActorIdentity?> GetIdentityCoreAsync(
        string actorId,
        CancellationToken ct) =>
        (await _envelopes.GetAsync(actorId, ct))?.Identity?.Clone();

    private async Task<string?> GetAgentKindCoreAsync(
        string actorId,
        CancellationToken ct) =>
        (await _envelopes.GetAsync(actorId, ct))?.Identity?.Kind;
}
