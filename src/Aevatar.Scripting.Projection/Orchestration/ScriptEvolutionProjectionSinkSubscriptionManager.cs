using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionSinkSubscriptionManager
    : IProjectionPortSinkSubscriptionManager<
        ScriptEvolutionRuntimeLease,
        IScriptEvolutionEventSink,
        ScriptEvolutionSessionCompletedEvent>
{
    private readonly IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent> _sessionEventHub;

    public ScriptEvolutionProjectionSinkSubscriptionManager(
        IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent> sessionEventHub)
    {
        _sessionEventHub = sessionEventHub ?? throw new ArgumentNullException(nameof(sessionEventHub));
    }

    public async Task AttachOrReplaceAsync(
        ScriptEvolutionRuntimeLease lease,
        IScriptEvolutionEventSink sink,
        Func<ScriptEvolutionSessionCompletedEvent, ValueTask> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(handler);
        ct.ThrowIfCancellationRequested();

        var streamSubscription = await _sessionEventHub.SubscribeAsync(
            lease.ActorId,
            lease.ProposalId,
            handler,
            ct);

        var previous = lease.AttachOrReplaceLiveSinkSubscription(sink, streamSubscription);
        if (previous != null)
            await previous.DisposeAsync();
    }

    public async Task DetachAsync(
        ScriptEvolutionRuntimeLease lease,
        IScriptEvolutionEventSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        var streamSubscription = lease.DetachLiveSinkSubscription(sink);
        if (streamSubscription != null)
            await streamSubscription.DisposeAsync();
    }
}
