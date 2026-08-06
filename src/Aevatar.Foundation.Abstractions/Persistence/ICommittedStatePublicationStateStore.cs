using Aevatar.Foundation.Abstractions.EventSourcing;

namespace Aevatar.Foundation.Abstractions.Persistence;

/// <summary>
/// Runtime persistence for committed-state publication progress.
/// Implementations must persist the Protobuf state and serialize updates per actor.
/// </summary>
public interface ICommittedStatePublicationStateStore
{
    /// <summary>Loads the runtime publication state, or <c>null</c> before initialization.</summary>
    Task<CommittedStatePublicationState?> LoadAsync(
        string actorId,
        CancellationToken ct = default);

    /// <summary>
    /// Initializes an absent checkpoint at a migration baseline. Concurrent initialization
    /// is idempotent and returns the already-persisted state.
    /// </summary>
    Task<CommittedStatePublicationState> InitializeAsync(
        string actorId,
        long baselinePublishedVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Advances a checkpoint after the publication adapter has accepted one committed fact.
    /// The current published version must equal <paramref name="expectedPublishedVersion"/>.
    /// </summary>
    Task<CommittedStatePublicationState> AdvanceAsync(
        string actorId,
        long expectedPublishedVersion,
        StateEvent publishedEvent,
        CancellationToken ct = default);

    /// <summary>
    /// Records an observable failed delivery attempt without advancing the checkpoint.
    /// </summary>
    Task<CommittedStatePublicationState> RecordFailureAsync(
        string actorId,
        long expectedPublishedVersion,
        StateEvent failedEvent,
        CommittedStatePublicationFailureStage stage,
        Exception error,
        CancellationToken ct = default);
}
