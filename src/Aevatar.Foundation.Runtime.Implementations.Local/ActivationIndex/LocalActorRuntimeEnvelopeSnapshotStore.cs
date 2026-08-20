using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Implementations.Local.ActivationIndex;

internal sealed class LocalActorRuntimeEnvelopeSnapshotStore<TState>
    : IEventSourcingSnapshotStore<TState>
    where TState : class, IMessage<TState>, new()
{
    private readonly ILocalActorRuntimeEnvelopeStore _envelopes;

    public LocalActorRuntimeEnvelopeSnapshotStore(ILocalActorRuntimeEnvelopeStore envelopes)
    {
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
    }

    public async Task<EventSourcingSnapshot<TState>?> LoadAsync(
        string agentId,
        CancellationToken ct = default)
    {
        var envelope = await _envelopes.GetAsync(agentId, ct);
        if (envelope?.StateSnapshot is not { Length: > 0 })
            return null;

        EnsureCompatibleStateType(envelope.StateContractTypeName);
        return new EventSourcingSnapshot<TState>(
            new MessageParser<TState>(() => new TState()).ParseFrom(envelope.StateSnapshot),
            envelope.StateSnapshotVersion);
    }

    public async Task SaveAsync(
        string agentId,
        EventSourcingSnapshot<TState> snapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        while (true)
        {
            var current = await _envelopes.GetAsync(agentId, ct);
            var next = current?.Clone() ?? new RuntimeActorStateEnvelope();
            next.StateContractTypeName = typeof(TState).FullName ?? typeof(TState).Name;
            next.StateSnapshot = ByteString.CopyFrom(snapshot.State.ToByteArray());
            next.StateSnapshotVersion = snapshot.Version;
            if (await _envelopes.CompareExchangeAsync(agentId, current, next, ct))
                return;
        }
    }

    private static void EnsureCompatibleStateType(string? storedTypeName)
    {
        var expected = typeof(TState).FullName ?? typeof(TState).Name;
        if (!string.Equals(storedTypeName, expected, StringComparison.Ordinal) &&
            !string.Equals(storedTypeName, typeof(TState).AssemblyQualifiedName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Local runtime snapshot type '{storedTypeName ?? "(missing)"}' does not match '{expected}'.");
        }
    }
}
