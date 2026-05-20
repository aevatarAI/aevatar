namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Hook invoked immediately before a committed state event is published to observers.
/// </summary>
// Refactor (iter18/cluster-006):
//   Old pattern: command-path projection activation facade with new actor/lifecycle phase
//   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
public interface ICommittedStatePublicationHook
{
    Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct);
}
