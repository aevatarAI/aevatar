using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Persistence;

/// <summary>Development and test implementation of runtime publication progress.</summary>
public sealed class InMemoryCommittedStatePublicationStateStore
    : ICommittedStatePublicationStateStore
{
    private readonly ConcurrentDictionary<string, CommittedStatePublicationState> _states =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<CommittedStatePublicationState?> LoadAsync(
        string actorId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(
                _states.TryGetValue(actorId, out var state) ? state.Clone() : null);
        }
    }

    public Task<CommittedStatePublicationState> InitializeAsync(
        string actorId,
        long baselinePublishedVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentOutOfRangeException.ThrowIfNegative(baselinePublishedVersion);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_states.TryGetValue(actorId, out var existing))
                return Task.FromResult(existing.Clone());

            var created = NewState(actorId, baselinePublishedVersion);
            _states[actorId] = created;
            return Task.FromResult(created.Clone());
        }
    }

    public Task<CommittedStatePublicationState> AdvanceAsync(
        string actorId,
        long expectedPublishedVersion,
        StateEvent publishedEvent,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(publishedEvent);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var state = GetInitialized(actorId);
            EnsureExpectedVersion(actorId, expectedPublishedVersion, state.PublishedVersion);
            EnsureNextEvent(actorId, expectedPublishedVersion, publishedEvent);

            state.PublishedVersion = publishedEvent.Version;
            state.PublishedEventId = publishedEvent.EventId;
            state.Revision++;
            state.UpdatedAt = Now();
            state.Failure = null;
            return Task.FromResult(state.Clone());
        }
    }

    public Task<CommittedStatePublicationState> RecordFailureAsync(
        string actorId,
        long expectedPublishedVersion,
        StateEvent failedEvent,
        CommittedStatePublicationFailureStage stage,
        Exception error,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(failedEvent);
        ArgumentNullException.ThrowIfNull(error);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var state = GetInitialized(actorId);
            EnsureExpectedVersion(actorId, expectedPublishedVersion, state.PublishedVersion);
            var previousAttempts = state.Failure?.Version == failedEvent.Version
                && string.Equals(state.Failure.EventId, failedEvent.EventId, StringComparison.Ordinal)
                    ? state.Failure.Attempts
                    : 0;
            state.Failure = BuildFailure(failedEvent, stage, error, previousAttempts + 1);
            state.Revision++;
            state.UpdatedAt = state.Failure.LastFailedAt;
            return Task.FromResult(state.Clone());
        }
    }

    private CommittedStatePublicationState GetInitialized(string actorId)
    {
        if (_states.TryGetValue(actorId, out var state) && state.Initialized)
            return state;

        throw new InvalidOperationException(
            $"Committed-state publication checkpoint for actor '{actorId}' is not initialized.");
    }

    internal static CommittedStatePublicationState NewState(string actorId, long baselineVersion) =>
        new()
        {
            ActorId = actorId,
            Initialized = true,
            PublishedVersion = baselineVersion,
            Revision = 1,
            UpdatedAt = Now(),
        };

    internal static void EnsureExpectedVersion(string actorId, long expected, long actual)
    {
        if (actual != expected)
            throw new CommittedStatePublicationStateConflictException(actorId, expected, actual);
    }

    internal static void EnsureNextEvent(
        string actorId,
        long expectedPublishedVersion,
        StateEvent publishedEvent)
    {
        if (!string.Equals(publishedEvent.AgentId, actorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Committed event '{publishedEvent.EventId}' belongs to actor " +
                $"'{publishedEvent.AgentId}', not checkpoint actor '{actorId}'.");
        }

        if (publishedEvent.Version != expectedPublishedVersion + 1)
        {
            throw new InvalidOperationException(
                $"Committed-state publication checkpoint for actor '{actorId}' must advance " +
                $"from version {expectedPublishedVersion} to {expectedPublishedVersion + 1}, " +
                $"not {publishedEvent.Version}.");
        }
    }

    internal static CommittedStatePublicationFailure BuildFailure(
        StateEvent failedEvent,
        CommittedStatePublicationFailureStage stage,
        Exception error,
        int attempts) =>
        new()
        {
            Version = failedEvent.Version,
            EventId = failedEvent.EventId,
            Attempts = attempts,
            ErrorType = error.GetType().FullName ?? error.GetType().Name,
            ErrorMessage = "Committed-state publication failed; inspect runtime logs for details.",
            LastFailedAt = Now(),
            Stage = stage,
        };

    private static Timestamp Now() => Timestamp.FromDateTime(DateTime.UtcNow);

}
