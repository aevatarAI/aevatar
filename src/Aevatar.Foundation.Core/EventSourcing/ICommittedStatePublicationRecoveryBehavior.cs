using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;

namespace Aevatar.Foundation.Core.EventSourcing;

internal interface ICommittedStatePublicationRecoveryBehavior
{
    IReadOnlyList<CommittedStateEventPublished> PendingCommittedStatePublications { get; }

    Task ConfirmPublicationAsync(StateEvent publishedEvent, CancellationToken ct = default);

    Task RecordPublicationFailureAsync(
        StateEvent failedEvent,
        CommittedStatePublicationFailureStage stage,
        Exception error,
        CancellationToken ct = default);
}
