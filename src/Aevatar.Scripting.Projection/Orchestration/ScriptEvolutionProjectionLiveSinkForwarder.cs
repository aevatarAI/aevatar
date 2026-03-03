using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Evolution;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionProjectionLiveSinkForwarder
    : IProjectionPortLiveSinkForwarder<
        ScriptEvolutionRuntimeLease,
        IScriptEvolutionEventSink,
        ScriptEvolutionSessionCompletedEvent>
{
    private readonly IProjectionPortSinkSubscriptionManager<
        ScriptEvolutionRuntimeLease,
        IScriptEvolutionEventSink,
        ScriptEvolutionSessionCompletedEvent> _sinkSubscriptionManager;

    public ScriptEvolutionProjectionLiveSinkForwarder(
        IProjectionPortSinkSubscriptionManager<
            ScriptEvolutionRuntimeLease,
            IScriptEvolutionEventSink,
            ScriptEvolutionSessionCompletedEvent> sinkSubscriptionManager)
    {
        _sinkSubscriptionManager = sinkSubscriptionManager ?? throw new ArgumentNullException(nameof(sinkSubscriptionManager));
    }

    public async ValueTask ForwardAsync(
        ScriptEvolutionRuntimeLease lease,
        IScriptEvolutionEventSink sink,
        ScriptEvolutionSessionCompletedEvent evt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(evt);
        ct.ThrowIfCancellationRequested();

        try
        {
            await sink.PushAsync(evt, CancellationToken.None);
        }
        catch (ScriptEvolutionEventSinkBackpressureException)
        {
            await _sinkSubscriptionManager.DetachAsync(lease, sink, CancellationToken.None);
        }
        catch (ScriptEvolutionEventSinkCompletedException)
        {
            await _sinkSubscriptionManager.DetachAsync(lease, sink, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            await _sinkSubscriptionManager.DetachAsync(lease, sink, CancellationToken.None);
        }
    }
}
