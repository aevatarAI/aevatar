using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.DynamicRuntime.Core.Agents;

public sealed class ScriptServiceDefinitionGAgent : GAgentBase<ScriptServiceDefinitionState>
{
    [EventHandler]
    public Task HandleRegisteredAsync(ScriptServiceRegisteredEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandleUpdatedAsync(ScriptServiceUpdatedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandleActivatedAsync(ScriptServiceActivatedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandleDeactivatedAsync(ScriptServiceDeactivatedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    protected override ScriptServiceDefinitionState TransitionState(ScriptServiceDefinitionState current, IMessage evt) =>
        StateTransitionMatcher.Match(current, evt)
            .On<ScriptServiceRegisteredEvent>(ApplyRegistered)
            .On<ScriptServiceUpdatedEvent>(ApplyUpdated)
            .On<ScriptServiceActivatedEvent>((state, e) => ApplyStatus(state, "Active", e.UpdatedAtUnixMs))
            .On<ScriptServiceDeactivatedEvent>((state, e) => ApplyStatus(state, "Inactive", e.UpdatedAtUnixMs))
            .OrCurrent();

    private static ScriptServiceDefinitionState ApplyRegistered(ScriptServiceDefinitionState current, ScriptServiceRegisteredEvent evt)
    {
        var next = current.Clone();
        next.ServiceId = evt.ServiceId ?? string.Empty;
        next.Version = evt.Version ?? string.Empty;
        next.ScriptCode = evt.ScriptCode ?? string.Empty;
        next.EntrypointType = evt.EntrypointType ?? string.Empty;
        next.ServiceMode = evt.ServiceMode ?? string.Empty;
        next.CapabilitiesHash = evt.CapabilitiesHash ?? string.Empty;
        next.UpdatedAtUnixMs = evt.UpdatedAtUnixMs;
        next.Status = "Inactive";
        next.PublicEndpoints.Clear();
        next.PublicEndpoints.AddRange(evt.PublicEndpoints);
        next.EventSubscriptions.Clear();
        next.EventSubscriptions.AddRange(evt.EventSubscriptions);
        return next;
    }

    private static ScriptServiceDefinitionState ApplyUpdated(ScriptServiceDefinitionState current, ScriptServiceUpdatedEvent evt)
    {
        var next = current.Clone();
        next.ServiceId = evt.ServiceId ?? current.ServiceId;
        next.Version = evt.Version ?? current.Version;
        next.ScriptCode = evt.ScriptCode ?? current.ScriptCode;
        next.EntrypointType = evt.EntrypointType ?? current.EntrypointType;
        next.ServiceMode = evt.ServiceMode ?? current.ServiceMode;
        next.CapabilitiesHash = evt.CapabilitiesHash ?? current.CapabilitiesHash;
        next.UpdatedAtUnixMs = evt.UpdatedAtUnixMs;
        next.PublicEndpoints.Clear();
        next.PublicEndpoints.AddRange(evt.PublicEndpoints);
        next.EventSubscriptions.Clear();
        next.EventSubscriptions.AddRange(evt.EventSubscriptions);
        return next;
    }

    private static ScriptServiceDefinitionState ApplyStatus(ScriptServiceDefinitionState current, string status, long updatedAtUnixMs)
    {
        var next = current.Clone();
        next.Status = status;
        next.UpdatedAtUnixMs = updatedAtUnixMs;
        return next;
    }
}
