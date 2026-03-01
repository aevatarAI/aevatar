using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed class ScriptDefinitionGAgent : GAgentBase<ScriptDefinitionState>
{
    public ScriptDefinitionGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleUpsertScriptDefinitionRequested(UpsertScriptDefinitionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        await PersistDomainEventAsync(new ScriptDefinitionUpsertedEvent
        {
            ScriptId = evt.ScriptId ?? string.Empty,
            ScriptRevision = evt.ScriptRevision ?? string.Empty,
            SourceText = evt.SourceText ?? string.Empty,
            SourceHash = evt.SourceHash ?? string.Empty,
        });
    }

    protected override ScriptDefinitionState TransitionState(ScriptDefinitionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptDefinitionUpsertedEvent>(ApplyDefinitionUpserted)
            .OrCurrent();

    private static ScriptDefinitionState ApplyDefinitionUpserted(
        ScriptDefinitionState state,
        ScriptDefinitionUpsertedEvent evt)
    {
        var next = state.Clone();
        next.ScriptId = evt.ScriptId ?? string.Empty;
        next.Revision = evt.ScriptRevision ?? string.Empty;
        next.SourceText = evt.SourceText ?? string.Empty;
        next.SourceHash = evt.SourceHash ?? string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = evt.ScriptRevision ?? string.Empty;
        return next;
    }
}
