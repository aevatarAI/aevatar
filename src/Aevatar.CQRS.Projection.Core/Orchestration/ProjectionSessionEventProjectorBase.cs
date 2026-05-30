namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Shared projector template for mapping one envelope into zero-or-more session events.
/// </summary>
// Refactor (iter367/cluster-issue377): Old pattern: session event entries used ScopeId for root actor routing.
// Refactor (iter367/cluster-issue377): Old pattern: projectors could preserve the alias into the hub call.
// Refactor (iter367/cluster-issue377): New principle: projector entries expose RootActorId explicitly.
// Refactor (iter367/cluster-issue377): New principle: hub fanout receives RootActorId + SessionId.
public abstract class ProjectionSessionEventProjectorBase<TContext, TEvent>
    : IProjectionProjector<TContext>
    where TContext : class, IProjectionSessionContext
    where TEvent : class
{
    private readonly IProjectionSessionEventHub<TEvent> _sessionEventHub;

    protected ProjectionSessionEventProjectorBase(IProjectionSessionEventHub<TEvent> sessionEventHub)
    {
        _sessionEventHub = sessionEventHub ?? throw new ArgumentNullException(nameof(sessionEventHub));
    }

    public async ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionDispatchRouteFilter.ShouldDispatch(envelope))
            return;

        var entries = ResolveSessionEventEntries(context, envelope);
        if (entries.Count == 0)
            return;

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.RootActorId) ||
                string.IsNullOrWhiteSpace(entry.SessionId) ||
                entry.Event == null)
            {
                continue;
            }

            await _sessionEventHub.PublishAsync(
                entry.RootActorId,
                entry.SessionId,
                entry.Event,
                ct);
        }
    }

    protected abstract IReadOnlyList<ProjectionSessionEventEntry<TEvent>> ResolveSessionEventEntries(
        TContext context,
        EventEnvelope envelope);

    protected static IReadOnlyList<ProjectionSessionEventEntry<TEvent>> EmptyEntries { get; } = [];
}

// Refactor (iter367/cluster-issue377): Old pattern: ProjectionSessionEventEntry.ScopeId meant root actor id.
// Refactor (iter367/cluster-issue377): Old pattern: the entry model carried a misleading first dimension.
// Refactor (iter367/cluster-issue377): New principle: RootActorId is the first routing invariant.
// Refactor (iter367/cluster-issue377): New principle: SessionId remains the second dimension for fanout.
public sealed record ProjectionSessionEventEntry<TEvent>(
    string RootActorId,
    string SessionId,
    TEvent Event)
    where TEvent : class;
