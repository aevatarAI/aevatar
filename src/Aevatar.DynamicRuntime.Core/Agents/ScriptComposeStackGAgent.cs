using Aevatar.DynamicRuntime.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.DynamicRuntime.Core.Agents;

public sealed class ScriptComposeStackGAgent : GAgentBase<ScriptComposeStackState>
{
    [EventHandler]
    public Task HandleAppliedAsync(ScriptComposeAppliedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    [EventHandler]
    public Task HandleConvergedAsync(ScriptComposeConvergedEvent evt, CancellationToken ct = default) => PersistDomainEventAsync(evt, ct);

    protected override ScriptComposeStackState TransitionState(ScriptComposeStackState current, IMessage evt) =>
        StateTransitionMatcher.Match(current, evt)
            .On<ScriptComposeAppliedEvent>(ApplyApplied)
            .On<ScriptComposeConvergedEvent>(ApplyConverged)
            .OrCurrent();

    private static ScriptComposeStackState ApplyApplied(ScriptComposeStackState current, ScriptComposeAppliedEvent evt)
    {
        var next = current.Clone();
        next.StackId = evt.StackId ?? string.Empty;
        next.ComposeSpecDigest = evt.ComposeSpecDigest ?? string.Empty;
        next.DesiredGeneration = evt.DesiredGeneration;
        next.ReconcileStatus = "Reconciling";
        return next;
    }

    private static ScriptComposeStackState ApplyConverged(ScriptComposeStackState current, ScriptComposeConvergedEvent evt)
    {
        var next = current.Clone();
        next.StackId = evt.StackId ?? current.StackId;
        next.ObservedGeneration = evt.ObservedGeneration;
        next.ReconcileStatus = "Converged";
        return next;
    }
}
