namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Raised when durable publication progress cannot be reconciled with committed events.
/// </summary>
public sealed class CommittedStatePublicationRecoveryException : Exception
{
    public CommittedStatePublicationRecoveryException(
        string actorId,
        long publishedVersion,
        long storeVersion,
        string reason)
        : base(
            $"Committed-state publication recovery failed for actor '{actorId}' " +
            $"(publishedVersion={publishedVersion}, storeVersion={storeVersion}): {reason}")
    {
        ActorId = actorId;
        PublishedVersion = publishedVersion;
        StoreVersion = storeVersion;
        Reason = reason;
    }

    public string ActorId { get; }

    public long PublishedVersion { get; }

    public long StoreVersion { get; }

    public string Reason { get; }
}
