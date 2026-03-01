using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed class ScriptRuntimeGAgent : GAgentBase<ScriptRuntimeState>
{
    public ScriptRuntimeGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleRunScriptRequested(RunScriptRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.DefinitionActorId))
            throw new InvalidOperationException("DefinitionActorId is required.");

        await PersistDomainEventAsync(new ScriptRunDomainEventCommitted
        {
            RunId = evt.RunId ?? string.Empty,
            ScriptRevision = evt.ScriptRevision ?? string.Empty,
            DefinitionActorId = evt.DefinitionActorId ?? string.Empty,
            EventType = "script.run.completed",
            PayloadJson = "{\"result\":\"ok\"}",
        });
    }

    protected override ScriptRuntimeState TransitionState(ScriptRuntimeState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptRunDomainEventCommitted>(ApplyCommitted)
            .OrCurrent();

    private static ScriptRuntimeState ApplyCommitted(
        ScriptRuntimeState state,
        ScriptRunDomainEventCommitted committed)
    {
        var next = state.Clone();
        next.DefinitionActorId = committed.DefinitionActorId ?? string.Empty;
        next.Revision = committed.ScriptRevision ?? string.Empty;
        next.LastRunId = committed.RunId ?? string.Empty;
        next.StatePayloadJson = committed.PayloadJson ?? string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = committed.RunId ?? string.Empty;
        return next;
    }
}
