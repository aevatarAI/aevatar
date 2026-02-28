using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.DynamicRuntime.Core.Agents;

public sealed class ScriptComposeServiceGAgent : GAgentBase<ScriptComposeServiceState>
{
    [EventHandler]
    public Task HandleScaledAsync(ScriptComposeServiceScaledEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandleRolledOutAsync(ScriptComposeServiceRolledOutEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandleDependencyBlockedAsync(ScriptComposeServiceDependencyBlockedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    protected override ScriptComposeServiceState TransitionState(ScriptComposeServiceState current, IMessage evt) =>
        StateTransitionMatcher.Match(current, evt)
            .On<ScriptComposeServiceScaledEvent>(Apply)
            .On<ScriptComposeServiceRolledOutEvent>(ApplyRolledOut)
            .OrCurrent();

    private static ScriptComposeServiceState Apply(ScriptComposeServiceState current, ScriptComposeServiceScaledEvent evt)
    {
        var next = current.Clone();
        next.StackId = evt.StackId ?? string.Empty;
        next.ServiceName = evt.ServiceName ?? string.Empty;
        next.ReplicasDesired = evt.ReplicasDesired;
        next.ServiceMode = evt.ServiceMode ?? string.Empty;
        next.ImageRef = evt.ImageRef ?? string.Empty;
        return next;
    }

    private static ScriptComposeServiceState ApplyRolledOut(ScriptComposeServiceState current, ScriptComposeServiceRolledOutEvent evt)
    {
        var next = current.Clone();
        next.StackId = evt.StackId ?? current.StackId;
        next.ServiceName = evt.ServiceName ?? current.ServiceName;
        next.ImageRef = evt.ImageRef ?? current.ImageRef;
        return next;
    }
}
