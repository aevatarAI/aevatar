namespace Aevatar.Foundation.Abstractions.Persistence;

/// <summary>Raised when a publication checkpoint update observes a different durable version.</summary>
public sealed class CommittedStatePublicationStateConflictException : Exception
{
    public CommittedStatePublicationStateConflictException(
        string actorId,
        long expectedPublishedVersion,
        long actualPublishedVersion)
        : base(
            $"Committed-state publication checkpoint conflict for actor '{actorId}': " +
            $"expected published version {expectedPublishedVersion}, actual {actualPublishedVersion}.")
    {
        ActorId = actorId;
        ExpectedPublishedVersion = expectedPublishedVersion;
        ActualPublishedVersion = actualPublishedVersion;
    }

    public string ActorId { get; }

    public long ExpectedPublishedVersion { get; }

    public long ActualPublishedVersion { get; }
}
