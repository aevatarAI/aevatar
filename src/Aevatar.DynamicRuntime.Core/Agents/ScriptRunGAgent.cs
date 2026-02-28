using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.DynamicRuntime.Core.Agents;

public sealed class ScriptRunGAgent : GAgentBase<ScriptRunState>
{
    [EventHandler]
    public Task HandleStartedAsync(ScriptRunStartedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandleCompletedAsync(ScriptRunCompletedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandleFailedAsync(ScriptRunFailedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    protected override ScriptRunState TransitionState(ScriptRunState current, IMessage evt) =>
        StateTransitionMatcher.Match(current, evt)
            .On<ScriptRunStartedEvent>(ApplyStarted)
            .On<ScriptRunCompletedEvent>(ApplyCompleted)
            .On<ScriptRunFailedEvent>(ApplyFailed)
            .OrCurrent();

    private static ScriptRunState ApplyStarted(ScriptRunState current, ScriptRunStartedEvent evt)
    {
        var next = current.Clone();
        next.RunId = evt.RunId ?? string.Empty;
        next.ContainerId = evt.ContainerId ?? string.Empty;
        next.Status = "Running";
        return next;
    }

    private static ScriptRunState ApplyCompleted(ScriptRunState current, ScriptRunCompletedEvent evt)
    {
        var next = current.Clone();
        next.RunId = evt.RunId ?? current.RunId;
        next.Status = "Succeeded";
        next.Result = evt.Result ?? string.Empty;
        next.Error = string.Empty;
        return next;
    }

    private static ScriptRunState ApplyFailed(ScriptRunState current, ScriptRunFailedEvent evt)
    {
        var next = current.Clone();
        next.RunId = evt.RunId ?? current.RunId;
        next.Status = "Failed";
        next.Error = evt.Error ?? string.Empty;
        return next;
    }
}
