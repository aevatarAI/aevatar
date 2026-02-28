using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.DynamicRuntime.Core.Agents;

public sealed class ScriptContainerGAgent : GAgentBase<ScriptContainerState>
{
    [EventHandler]
    public Task HandleCreatedAsync(ScriptContainerCreatedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandleStartedAsync(ScriptContainerStartedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandleStoppedAsync(ScriptContainerStoppedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandleDestroyedAsync(ScriptContainerDestroyedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    protected override ScriptContainerState TransitionState(ScriptContainerState current, IMessage evt) =>
        StateTransitionMatcher.Match(current, evt)
            .On<ScriptContainerCreatedEvent>(ApplyCreated)
            .On<ScriptContainerStartedEvent>((state, _) => ApplyStatus(state, "Running"))
            .On<ScriptContainerStoppedEvent>((state, _) => ApplyStatus(state, "Stopped"))
            .On<ScriptContainerDestroyedEvent>((state, _) => ApplyStatus(state, "Destroyed"))
            .OrCurrent();

    private static ScriptContainerState ApplyCreated(ScriptContainerState current, ScriptContainerCreatedEvent evt)
    {
        var next = current.Clone();
        next.ContainerId = evt.ContainerId ?? string.Empty;
        next.StackId = evt.StackId ?? string.Empty;
        next.ServiceName = evt.ServiceName ?? string.Empty;
        next.ImageDigest = evt.ImageDigest ?? string.Empty;
        next.RoleActorId = evt.RoleActorId ?? string.Empty;
        next.Status = "Created";
        return next;
    }

    private static ScriptContainerState ApplyStatus(ScriptContainerState current, string status)
    {
        var next = current.Clone();
        next.Status = status;
        return next;
    }
}
