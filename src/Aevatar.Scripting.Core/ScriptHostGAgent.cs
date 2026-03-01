using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed class ScriptHostGAgent : GAgentBase<ScriptHostState>
{
    public ScriptHostGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleRunScriptRequested(RunScriptRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        await PersistDomainEventAsync(new ScriptDomainEventCommitted
        {
            RunId = evt.RunId,
            ScriptId = State.ScriptId,
            ScriptRevision = evt.ScriptRevision,
            EventType = "script.run.completed",
            PayloadJson = "{\"result\":\"ok\"}",
        });
    }

    protected override ScriptHostState TransitionState(ScriptHostState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptDomainEventCommitted>(ApplyCommitted)
            .OrCurrent();

    private static ScriptHostState ApplyCommitted(
        ScriptHostState state,
        ScriptDomainEventCommitted committed)
    {
        var next = state.Clone();
        if (!string.IsNullOrWhiteSpace(committed.ScriptId))
            next.ScriptId = committed.ScriptId;

        next.Revision = committed.ScriptRevision ?? string.Empty;
        next.StatePayloadJson = committed.PayloadJson ?? string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = committed.RunId ?? string.Empty;
        return next;
    }
}
