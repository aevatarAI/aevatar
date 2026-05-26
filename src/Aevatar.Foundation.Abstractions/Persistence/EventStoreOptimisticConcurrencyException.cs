namespace Aevatar.Foundation.Abstractions.Persistence;

/// <summary>
/// Raised when an event-store append observes a different stream version than the caller expected.
/// </summary>
public sealed class EventStoreOptimisticConcurrencyException : InvalidOperationException
{
    public EventStoreOptimisticConcurrencyException(
        string agentId,
        long expectedVersion,
        long actualVersion)
        : base($"Optimistic concurrency conflict: expected {expectedVersion}, actual {actualVersion}")
    {
        AgentId = agentId ?? string.Empty;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public string AgentId { get; }

    public long ExpectedVersion { get; }

    public long ActualVersion { get; }
}
