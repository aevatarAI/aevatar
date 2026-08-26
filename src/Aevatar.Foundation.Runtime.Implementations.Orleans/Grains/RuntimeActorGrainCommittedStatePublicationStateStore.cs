using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

/// <summary>
/// Stores publication progress in a dedicated Orleans row while retaining the
/// legacy runtime-row payload as a rolling-upgrade reconciliation source.
/// </summary>
internal sealed class RuntimeActorGrainCommittedStatePublicationStateStore
    : ICommittedStatePublicationStateStore
{
    private readonly IPersistentState<RuntimeActorGrainState> _runtimeState;
    private readonly IPersistentState<RuntimeActorCommittedStatePublicationGrainState> _publicationState;

    public RuntimeActorGrainCommittedStatePublicationStateStore(
        IPersistentState<RuntimeActorGrainState> runtimeState,
        IPersistentState<RuntimeActorCommittedStatePublicationGrainState> publicationState)
    {
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        _publicationState = publicationState ?? throw new ArgumentNullException(nameof(publicationState));
    }

    public RuntimeActorGrainCommittedStatePublicationStateStore(
        IRuntimeActorStateBindingAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        _runtimeState = accessor.Current
            ?? throw new InvalidOperationException(
                "Runtime actor state is not bound. Resolve publication state only within RuntimeActorGrain binding context.");
        _publicationState = accessor.CurrentCommittedStatePublication
            ?? throw new InvalidOperationException(
                "Runtime actor publication state is not bound. Resolve publication state only within RuntimeActorGrain binding context.");
    }

    public async Task<CommittedStatePublicationState?> LoadAsync(
        string actorId,
        CancellationToken ct = default)
    {
        ValidateActor(actorId, ct);
        return await ReadReconciledAsync(actorId);
    }

    public async Task<CommittedStatePublicationState> InitializeAsync(
        string actorId,
        long baselinePublishedVersion,
        CancellationToken ct = default)
    {
        ValidateActor(actorId, ct);
        ArgumentOutOfRangeException.ThrowIfNegative(baselinePublishedVersion);
        var existing = await ReadReconciledAsync(actorId);
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
        var state = await GetInitializedAsync(actorId);
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
        var state = await GetInitializedAsync(actorId);
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

    private async Task<CommittedStatePublicationState?> ReadReconciledAsync(string actorId)
    {
        var legacy = Parse(_runtimeState.State.CommittedStatePublicationState);
        var dedicated = Parse(_publicationState.State.Checkpoint);
        ValidateStoredActor(actorId, legacy, "legacy runtime");
        ValidateStoredActor(actorId, dedicated, "dedicated publication");

        var selected = SelectNewest(actorId, legacy, dedicated);
        if (selected == null)
            return null;

        if (ReferenceEquals(selected, legacy))
            await WriteAsync(selected);
        else
            UpdateLegacyShadow(selected);

        return selected.Clone();
    }

    private async Task<CommittedStatePublicationState> GetInitializedAsync(string actorId)
    {
        var state = await ReadReconciledAsync(actorId);
        if (state?.Initialized == true)
            return state;

        throw new InvalidOperationException(
            $"Committed-state publication checkpoint for actor '{actorId}' is not initialized.");
    }

    private async Task WriteAsync(CommittedStatePublicationState state)
    {
        var previous = _publicationState.State.Checkpoint;
        _publicationState.State.Checkpoint = state.ToByteArray();
        try
        {
            await _publicationState.WriteStateAsync();
            UpdateLegacyShadow(state);
        }
        catch
        {
            if (await TryConfirmCommittedWriteAsync(state, previous))
            {
                UpdateLegacyShadow(state);
                return;
            }

            throw;
        }
    }

    private async Task<bool> TryConfirmCommittedWriteAsync(
        CommittedStatePublicationState expected,
        byte[]? previous)
    {
        try
        {
            await _publicationState.ReadStateAsync();
            return expected.Equals(Parse(_publicationState.State.Checkpoint));
        }
        catch
        {
            // Preserve the last locally known checkpoint when even the read-back
            // outcome is unavailable. The activation will be shed and rehydrate.
            _publicationState.State.Checkpoint = previous;
            return false;
        }
    }

    private static CommittedStatePublicationState? Parse(byte[]? payload) =>
        payload is { Length: > 0 }
            ? CommittedStatePublicationState.Parser.ParseFrom(payload)
            : null;

    private static CommittedStatePublicationState? SelectNewest(
        string actorId,
        CommittedStatePublicationState? legacy,
        CommittedStatePublicationState? dedicated)
    {
        if (legacy == null)
            return dedicated;
        if (dedicated == null)
            return legacy;

        if (legacy.PublishedVersion == dedicated.PublishedVersion)
        {
            if (legacy.Initialized != dedicated.Initialized ||
                !string.Equals(
                    legacy.PublishedEventId,
                    dedicated.PublishedEventId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Committed-state publication checkpoints for actor '{actorId}' disagree " +
                    $"at published version {legacy.PublishedVersion}.");
            }

            if (legacy.Revision == dedicated.Revision && !legacy.Equals(dedicated))
            {
                throw new InvalidOperationException(
                    $"Committed-state publication checkpoints for actor '{actorId}' are ambiguous " +
                    $"at published version {legacy.PublishedVersion}, revision {legacy.Revision}.");
            }
        }

        var versionComparison = legacy.PublishedVersion.CompareTo(dedicated.PublishedVersion);
        if (versionComparison != 0)
            return versionComparison > 0 ? legacy : dedicated;

        return legacy.Revision > dedicated.Revision ? legacy : dedicated;
    }

    private void UpdateLegacyShadow(CommittedStatePublicationState state)
    {
        // Do not write the runtime row here: that is the amplification boundary this
        // store removes. The next ordinary compact snapshot/watermark write carries
        // this rollback shadow for older binaries. Until then, rollback can observe
        // the older durable legacy value; forward reconciliation always selects the
        // higher publication version/revision and repairs the dedicated row.
        _runtimeState.State.CommittedStatePublicationState = state.ToByteArray();
    }

    private static void ValidateStoredActor(
        string actorId,
        CommittedStatePublicationState? state,
        string source)
    {
        if (state == null)
            return;

        if (!string.Equals(state.ActorId, actorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {source} committed-state publication checkpoint belongs to actor " +
                $"'{state.ActorId}', not '{actorId}'.");
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
