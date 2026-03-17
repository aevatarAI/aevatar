namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Shared projector template for mapping one envelope into zero-or-more session events.
/// </summary>
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
            if (string.IsNullOrWhiteSpace(entry.ScopeId) ||
                string.IsNullOrWhiteSpace(entry.SessionId) ||
                entry.Event == null)
            {
                continue;
            }

            await _sessionEventHub.PublishAsync(
                entry.ScopeId,
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

public sealed record ProjectionSessionEventEntry<TEvent>(
    string ScopeId,
    string SessionId,
    TEvent Event)
    where TEvent : class;
