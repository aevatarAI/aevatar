using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed class ScriptCatalogGAgent : GAgentBase<ScriptCatalogState>
{
    public ScriptCatalogGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandlePromoteScriptRevisionRequested(PromoteScriptRevisionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var scriptId = evt.ScriptId ?? string.Empty;
        var revision = evt.Revision ?? string.Empty;
        var expectedBaseRevision = evt.ExpectedBaseRevision ?? string.Empty;

        if (string.IsNullOrWhiteSpace(scriptId))
            throw new InvalidOperationException("ScriptId is required.");
        if (string.IsNullOrWhiteSpace(revision))
            throw new InvalidOperationException("Revision is required.");
        if (!string.IsNullOrWhiteSpace(State.ScopeId) &&
            !string.IsNullOrWhiteSpace(evt.ScopeId) &&
            !string.Equals(State.ScopeId, evt.ScopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Script catalog actor `{Id}` is already bound to scope `{State.ScopeId}` and cannot switch to `{evt.ScopeId}`.");
        }

        if (!string.IsNullOrWhiteSpace(expectedBaseRevision))
        {
            if (State.Entries.TryGetValue(scriptId, out var currentEntry) &&
                !string.Equals(currentEntry.ActiveRevision, expectedBaseRevision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Promotion conflict for script `{scriptId}`. expected_base_revision=`{expectedBaseRevision}` actual_active_revision=`{currentEntry.ActiveRevision}`.");
            }
        }

        await PersistDomainEventAsync(new ScriptCatalogRevisionPromotedEvent
        {
            ScriptId = scriptId,
            Revision = revision,
            DefinitionActorId = evt.DefinitionActorId ?? string.Empty,
            SourceHash = evt.SourceHash ?? string.Empty,
            ProposalId = evt.ProposalId ?? string.Empty,
            ScopeId = evt.ScopeId ?? string.Empty,
        });
    }

    [EventHandler]
    public async Task HandleRollbackScriptRevisionRequested(RollbackScriptRevisionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var scriptId = evt.ScriptId ?? string.Empty;
        var targetRevision = evt.TargetRevision ?? string.Empty;
        var expectedCurrentRevision = evt.ExpectedCurrentRevision ?? string.Empty;
        if (string.IsNullOrWhiteSpace(scriptId))
            throw new InvalidOperationException("ScriptId is required.");
        if (string.IsNullOrWhiteSpace(targetRevision))
            throw new InvalidOperationException("TargetRevision is required.");
        if (!string.IsNullOrWhiteSpace(State.ScopeId) &&
            !string.IsNullOrWhiteSpace(evt.ScopeId) &&
            !string.Equals(State.ScopeId, evt.ScopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Script catalog actor `{Id}` is already bound to scope `{State.ScopeId}` and cannot switch to `{evt.ScopeId}`.");
        }

        if (!State.Entries.TryGetValue(scriptId, out var entry))
            throw new InvalidOperationException($"Script `{scriptId}` does not exist in catalog.");

        if (!string.IsNullOrWhiteSpace(expectedCurrentRevision) &&
            !string.Equals(entry.ActiveRevision, expectedCurrentRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Rollback conflict for script `{scriptId}`. expected_current_revision=`{expectedCurrentRevision}` actual_active_revision=`{entry.ActiveRevision}`.");
        }

        var existsInHistory = entry.RevisionHistory.Any(x =>
            string.Equals(x, targetRevision, StringComparison.Ordinal));
        if (!existsInHistory && !string.Equals(entry.ActiveRevision, targetRevision, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Target revision `{targetRevision}` does not exist in script catalog for `{scriptId}`.");

        var previousRevision = entry.ActiveRevision ?? string.Empty;

        await PersistDomainEventAsync(new ScriptCatalogRollbackRequestedEvent
        {
            ScriptId = scriptId,
            TargetRevision = targetRevision,
            Reason = evt.Reason ?? string.Empty,
            ProposalId = evt.ProposalId ?? string.Empty,
            ScopeId = evt.ScopeId ?? string.Empty,
        });

        await PersistDomainEventAsync(new ScriptCatalogRolledBackEvent
        {
            ScriptId = scriptId,
            TargetRevision = targetRevision,
            PreviousRevision = previousRevision,
            ProposalId = evt.ProposalId ?? string.Empty,
            ScopeId = evt.ScopeId ?? string.Empty,
        });
    }

    protected override ScriptCatalogState TransitionState(ScriptCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptCatalogRevisionPromotedEvent>(ApplyPromoted)
            .On<ScriptCatalogRollbackRequestedEvent>(ApplyRollbackRequested)
            .On<ScriptCatalogRolledBackEvent>(ApplyRolledBack)
            .OrCurrent();

    private static ScriptCatalogState ApplyPromoted(
        ScriptCatalogState state,
        ScriptCatalogRevisionPromotedEvent evt)
    {
        var next = state.Clone();
        var scriptId = evt.ScriptId ?? string.Empty;
        var entry = GetOrCreateEntry(next, scriptId);

        entry.ScriptId = scriptId;
        entry.ScopeId = string.IsNullOrWhiteSpace(evt.ScopeId) ? state.ScopeId : evt.ScopeId;
        entry.PreviousRevision = entry.ActiveRevision ?? string.Empty;
        entry.ActiveRevision = evt.Revision ?? string.Empty;
        entry.ActiveDefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        entry.ActiveSourceHash = evt.SourceHash ?? string.Empty;
        entry.LastProposalId = evt.ProposalId ?? string.Empty;

        if (!entry.RevisionHistory.Any(x => string.Equals(x, entry.ActiveRevision, StringComparison.Ordinal)))
            entry.RevisionHistory.Add(entry.ActiveRevision);

        next.ScopeId = string.IsNullOrWhiteSpace(evt.ScopeId) ? state.ScopeId : evt.ScopeId;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(scriptId, ":", entry.ActiveRevision, ":promoted");
        return next;
    }

    private static ScriptCatalogState ApplyRollbackRequested(
        ScriptCatalogState state,
        ScriptCatalogRollbackRequestedEvent evt)
    {
        var next = state.Clone();
        next.ScopeId = string.IsNullOrWhiteSpace(evt.ScopeId) ? state.ScopeId : evt.ScopeId;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(
            evt.ScriptId ?? string.Empty,
            ":",
            evt.TargetRevision ?? string.Empty,
            ":rollback-requested");
        return next;
    }

    private static ScriptCatalogState ApplyRolledBack(
        ScriptCatalogState state,
        ScriptCatalogRolledBackEvent evt)
    {
        var next = state.Clone();
        var scriptId = evt.ScriptId ?? string.Empty;
        var targetRevision = evt.TargetRevision ?? string.Empty;
        var entry = GetOrCreateEntry(next, scriptId);
        var previouslyActiveRevision = entry.ActiveRevision ?? string.Empty;
        var previouslyActiveDefinitionActorId = entry.ActiveDefinitionActorId ?? string.Empty;
        var previouslyActiveSourceHash = entry.ActiveSourceHash ?? string.Empty;

        entry.ScriptId = scriptId;
        entry.ScopeId = string.IsNullOrWhiteSpace(evt.ScopeId) ? state.ScopeId : evt.ScopeId;
        entry.PreviousRevision = evt.PreviousRevision ?? previouslyActiveRevision;
        entry.ActiveRevision = targetRevision;
        if (string.Equals(targetRevision, previouslyActiveRevision, StringComparison.Ordinal))
        {
            entry.ActiveDefinitionActorId = previouslyActiveDefinitionActorId;
            entry.ActiveSourceHash = previouslyActiveSourceHash;
        }
        else
        {
            entry.ActiveDefinitionActorId = string.Empty;
            entry.ActiveSourceHash = string.Empty;
        }

        entry.LastProposalId = evt.ProposalId ?? string.Empty;

        if (!entry.RevisionHistory.Any(x => string.Equals(x, targetRevision, StringComparison.Ordinal)))
            entry.RevisionHistory.Add(targetRevision);

        next.ScopeId = string.IsNullOrWhiteSpace(evt.ScopeId) ? state.ScopeId : evt.ScopeId;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(scriptId, ":", targetRevision, ":rolled-back");
        return next;
    }

    private static ScriptCatalogEntryState GetOrCreateEntry(ScriptCatalogState state, string scriptId)
    {
        if (!state.Entries.TryGetValue(scriptId, out var entry))
        {
            entry = new ScriptCatalogEntryState { ScriptId = scriptId };
            state.Entries[scriptId] = entry;
        }

        return entry;
    }
}
