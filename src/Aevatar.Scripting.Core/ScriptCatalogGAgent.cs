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
        });

        await PersistDomainEventAsync(new ScriptCatalogRolledBackEvent
        {
            ScriptId = scriptId,
            TargetRevision = targetRevision,
            PreviousRevision = previousRevision,
            ProposalId = evt.ProposalId ?? string.Empty,
        });
    }

    [EventHandler]
    public async Task HandleQueryScriptCatalogEntryRequested(QueryScriptCatalogEntryRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return;

        if (string.IsNullOrWhiteSpace(evt.ScriptId))
        {
            await SendQueryResponseAsync(evt.ReplyStreamId, new ScriptCatalogEntryRespondedEvent
            {
                RequestId = evt.RequestId,
                Found = false,
                FailureReason = "ScriptId is required.",
            });
            return;
        }

        if (!State.Entries.TryGetValue(evt.ScriptId, out var entry))
        {
            await SendQueryResponseAsync(evt.ReplyStreamId, new ScriptCatalogEntryRespondedEvent
            {
                RequestId = evt.RequestId,
                Found = false,
                ScriptId = evt.ScriptId,
                FailureReason = $"Script `{evt.ScriptId}` not found in catalog.",
            });
            return;
        }

        var responded = new ScriptCatalogEntryRespondedEvent
        {
            RequestId = evt.RequestId,
            Found = true,
            ScriptId = entry.ScriptId ?? string.Empty,
            ActiveRevision = entry.ActiveRevision ?? string.Empty,
            ActiveDefinitionActorId = entry.ActiveDefinitionActorId ?? string.Empty,
            ActiveSourceHash = entry.ActiveSourceHash ?? string.Empty,
            PreviousRevision = entry.PreviousRevision ?? string.Empty,
            LastProposalId = entry.LastProposalId ?? string.Empty,
        };
        responded.RevisionHistory.Add(entry.RevisionHistory);
        await SendQueryResponseAsync(evt.ReplyStreamId, responded);
    }

    protected override ScriptCatalogState TransitionState(ScriptCatalogState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptCatalogRevisionPromotedEvent>(ApplyPromoted)
            .On<ScriptCatalogRollbackRequestedEvent>(ApplyRollbackRequested)
            .On<ScriptCatalogRolledBackEvent>(ApplyRolledBack)
            .OrCurrent();

    private Task SendQueryResponseAsync(
        string replyStreamId,
        ScriptCatalogEntryRespondedEvent response,
        CancellationToken ct = default)
    {
        return EventPublisher.SendToAsync(replyStreamId, response, ct, sourceEnvelope: null);
    }

    private static ScriptCatalogState ApplyPromoted(
        ScriptCatalogState state,
        ScriptCatalogRevisionPromotedEvent evt)
    {
        var next = state.Clone();
        var scriptId = evt.ScriptId ?? string.Empty;
        var entry = GetOrCreateEntry(next, scriptId);

        entry.ScriptId = scriptId;
        entry.PreviousRevision = entry.ActiveRevision ?? string.Empty;
        entry.ActiveRevision = evt.Revision ?? string.Empty;
        entry.ActiveDefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        entry.ActiveSourceHash = evt.SourceHash ?? string.Empty;
        entry.LastProposalId = evt.ProposalId ?? string.Empty;

        if (!entry.RevisionHistory.Any(x => string.Equals(x, entry.ActiveRevision, StringComparison.Ordinal)))
            entry.RevisionHistory.Add(entry.ActiveRevision);

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(scriptId, ":", entry.ActiveRevision, ":promoted");
        return next;
    }

    private static ScriptCatalogState ApplyRollbackRequested(
        ScriptCatalogState state,
        ScriptCatalogRollbackRequestedEvent evt)
    {
        var next = state.Clone();
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
