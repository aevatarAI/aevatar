namespace Aevatar.Foundation.Core.EventSourcing;

/// <summary>
/// Hook invoked immediately before a committed state event is published to observers.
/// </summary>
public interface ICommittedStatePublicationHook
{
    Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct);
}
