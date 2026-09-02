using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

/// <summary>
/// Stores publication progress inside the owning Orleans actor's durable runtime state.
/// </summary>
internal sealed class RuntimeActorGrainCommittedStatePublicationStateStore
    : ICommittedStatePublicationStateStore
{
    private readonly IPersistentState<RuntimeActorGrainState> _runtimeState;

    public RuntimeActorGrainCommittedStatePublicationStateStore(
        IPersistentState<RuntimeActorGrainState> runtimeState)
    {
        _runtimeState = runtimeState;
    }

    public RuntimeActorGrainCommittedStatePublicationStateStore(
        IRuntimeActorStateBindingAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        _runtimeState = accessor.Current
            ?? throw new InvalidOperationException(
                "Runtime actor state is not bound. Resolve publication state only within RuntimeActorGrain binding context.");
    }

    public Task<CommittedStatePublicationState?> LoadAsync(
        string actorId,
        CancellationToken ct = default)
    {
        ValidateActor(actorId, ct);
        return Task.FromResult(Read());
    }

    public async Task<CommittedStatePublicationState> InitializeAsync(
        string actorId,
        long baselinePublishedVersion,
        CancellationToken ct = default)
    {
        ValidateActor(actorId, ct);
        ArgumentOutOfRangeException.ThrowIfNegative(baselinePublishedVersion);
        var existing = Read();
        if (existing != null)
            return existing;

        var created = new CommittedStatePublicationState
        {
            ActorId = actorId,
            Initialized = true,
            PublishedVersion = baselinePublishedVersion,
            Revision = 1,
            UpdatedAt = Now(),
        };
        await WriteAsync(created);
        return created.Clone();
    }

    public async Task<CommittedStatePublicationState> AdvanceAsync(
        string actorId,
        long expectedPublishedVersion,
        StateEvent publishedEvent,
        CancellationToken ct = default)
    {
        ValidateActor(actorId, ct);
        ArgumentNullException.ThrowIfNull(publishedEvent);
        var state = GetInitialized(actorId);
        EnsureExpected(actorId, expectedPublishedVersion, state.PublishedVersion);
        EnsureNext(actorId, expectedPublishedVersion, publishedEvent);
        state.PublishedVersion = publishedEvent.Version;
        state.PublishedEventId = publishedEvent.EventId;
        state.Revision++;
        state.UpdatedAt = Now();
        state.Failure = null;
        await WriteAsync(state);
        return state.Clone();
    }

    public async Task<CommittedStatePublicationState> RecordFailureAsync(
        string actorId,
        long expectedPublishedVersion,
        StateEvent failedEvent,
        CommittedStatePublicationFailureStage stage,
        Exception error,
        CancellationToken ct = default)
    {
        ValidateActor(actorId, ct);
        ArgumentNullException.ThrowIfNull(failedEvent);
        ArgumentNullException.ThrowIfNull(error);
        var state = GetInitialized(actorId);
        EnsureExpected(actorId, expectedPublishedVersion, state.PublishedVersion);
        var previousAttempts = state.Failure?.Version == failedEvent.Version
            && string.Equals(state.Failure.EventId, failedEvent.EventId, StringComparison.Ordinal)
                ? state.Failure.Attempts
                : 0;
        state.Failure = new CommittedStatePublicationFailure
        {
            Version = failedEvent.Version,
            EventId = failedEvent.EventId,
            Attempts = previousAttempts + 1,
            ErrorType = error.GetType().FullName ?? error.GetType().Name,
            ErrorMessage = "Committed-state publication failed; inspect runtime logs for details.",
            LastFailedAt = Now(),
            Stage = stage,
        };
        state.Revision++;
        state.UpdatedAt = state.Failure.LastFailedAt;
        await WriteAsync(state);
        return state.Clone();
    }

    private CommittedStatePublicationState? Read()
    {
        var payload = _runtimeState.State.CommittedStatePublicationState;
        return payload is { Length: > 0 }
            ? CommittedStatePublicationState.Parser.ParseFrom(payload)
            : null;
    }

    private CommittedStatePublicationState GetInitialized(string actorId)
    {
        var state = Read();
        if (state?.Initialized == true)
            return state;

        throw new InvalidOperationException(
            $"Committed-state publication checkpoint for actor '{actorId}' is not initialized.");
    }

    private async Task WriteAsync(CommittedStatePublicationState state)
    {
        var previous = _runtimeState.State.CommittedStatePublicationState;
        _runtimeState.State.CommittedStatePublicationState = state.ToByteArray();
        try
        {
            await _runtimeState.WriteStateAsync();
        }
        catch
        {
            _runtimeState.State.CommittedStatePublicationState = previous;
            throw;
        }
    }

    private static void EnsureExpected(string actorId, long expected, long actual)
    {
        if (actual != expected)
            throw new CommittedStatePublicationStateConflictException(actorId, expected, actual);
    }

    private static void EnsureNext(string actorId, long expected, StateEvent publishedEvent)
    {
        if (!string.Equals(actorId, publishedEvent.AgentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Committed event '{publishedEvent.EventId}' belongs to actor " +
                $"'{publishedEvent.AgentId}', not checkpoint actor '{actorId}'.");
        }

        if (publishedEvent.Version != expected + 1)
        {
            throw new InvalidOperationException(
                $"Committed-state publication checkpoint for actor '{actorId}' must advance " +
                $"from version {expected} to {expected + 1}, not {publishedEvent.Version}.");
        }
    }

    private void ValidateActor(string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(_runtimeState.State.AgentId)
            && !string.Equals(_runtimeState.State.AgentId, actorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Runtime publication state for actor '{_runtimeState.State.AgentId}' cannot serve '{actorId}'.");
        }
    }

    private static Timestamp Now() => Timestamp.FromDateTime(DateTime.UtcNow);

}
