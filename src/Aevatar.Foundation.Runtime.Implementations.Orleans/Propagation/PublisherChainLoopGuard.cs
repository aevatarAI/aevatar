namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Propagation;

/// <summary>
/// Loop guard based on publisher chain metadata.
/// </summary>
public sealed class PublisherChainLoopGuard : IEventLoopGuard
{
    public void BeforeDispatch(string senderActorId, string targetActorId, EventEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.Equals(senderActorId, targetActorId, StringComparison.Ordinal))
            return;

        if (!envelope.Metadata.TryGetValue(OrleansRuntimeConstants.PublishersMetadataKey, out var chain) ||
            string.IsNullOrWhiteSpace(chain))
        {
            envelope.Metadata[OrleansRuntimeConstants.PublishersMetadataKey] = senderActorId;
            return;
        }

        var ids = chain.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Contains(senderActorId, StringComparer.Ordinal))
            return;

        envelope.Metadata[OrleansRuntimeConstants.PublishersMetadataKey] = $"{chain},{senderActorId}";
    }

    public bool ShouldDrop(string selfActorId, EventEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfActorId);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!envelope.Metadata.TryGetValue(OrleansRuntimeConstants.PublishersMetadataKey, out var chain) ||
            string.IsNullOrWhiteSpace(chain))
        {
            return false;
        }

        var ids = chain.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ids.Contains(selfActorId, StringComparer.Ordinal);
    }
}
