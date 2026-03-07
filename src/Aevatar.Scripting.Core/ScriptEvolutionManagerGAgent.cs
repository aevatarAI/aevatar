using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed class ScriptEvolutionManagerGAgent : GAgentBase<ScriptEvolutionManagerState>
{
    private readonly IScriptEvolutionFlowPort _evolutionFlowPort;

    public ScriptEvolutionManagerGAgent(IScriptEvolutionFlowPort evolutionFlowPort)
    {
        _evolutionFlowPort = evolutionFlowPort ?? throw new ArgumentNullException(nameof(evolutionFlowPort));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleScriptEvolutionProposalIndexed(ScriptEvolutionProposalIndexedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.ProposalId) ||
            string.IsNullOrWhiteSpace(evt.ScriptId) ||
            string.IsNullOrWhiteSpace(evt.SessionActorId))
        {
            return;
        }

        await PersistDomainEventAsync(new ScriptEvolutionProposalIndexedEvent
        {
            ProposalId = evt.ProposalId,
            ScriptId = evt.ScriptId,
            SessionActorId = evt.SessionActorId,
        });
    }

    [EventHandler]
    public async Task HandleScriptEvolutionRollbackRequested(ScriptEvolutionRollbackRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        await PersistDomainEventAsync(new ScriptEvolutionRollbackRequestedEvent
        {
            ProposalId = evt.ProposalId ?? string.Empty,
            ScriptId = evt.ScriptId ?? string.Empty,
            TargetRevision = evt.TargetRevision ?? string.Empty,
            CatalogActorId = evt.CatalogActorId ?? string.Empty,
            Reason = evt.Reason ?? string.Empty,
        });

        await _evolutionFlowPort.RollbackAsync(
            new ScriptRollbackRequest(
                ProposalId: evt.ProposalId ?? string.Empty,
                ScriptId: evt.ScriptId ?? string.Empty,
                TargetRevision: evt.TargetRevision ?? string.Empty,
                CatalogActorId: evt.CatalogActorId ?? string.Empty,
                Reason: evt.Reason ?? string.Empty,
                ExpectedCurrentRevision: string.Empty),
            CancellationToken.None);

        await PersistDomainEventAsync(new ScriptEvolutionRolledBackEvent
        {
            ProposalId = evt.ProposalId ?? string.Empty,
            ScriptId = evt.ScriptId ?? string.Empty,
            TargetRevision = evt.TargetRevision ?? string.Empty,
            PreviousRevision = string.Empty,
            CatalogActorId = evt.CatalogActorId ?? string.Empty,
        });
    }

    protected override ScriptEvolutionManagerState TransitionState(
        ScriptEvolutionManagerState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptEvolutionProposalIndexedEvent>(ApplyIndexed)
            .On<ScriptEvolutionRollbackRequestedEvent>(ApplyStamped)
            .On<ScriptEvolutionRolledBackEvent>(ApplyStamped)
            .OrCurrent();

    private static ScriptEvolutionManagerState ApplyIndexed(
        ScriptEvolutionManagerState state,
        ScriptEvolutionProposalIndexedEvent evt)
    {
        var next = state.Clone();
        if (!string.IsNullOrWhiteSpace(evt.ProposalId) && !string.IsNullOrWhiteSpace(evt.SessionActorId))
            next.ProposalSessionActorIds[evt.ProposalId] = evt.SessionActorId;
        if (!string.IsNullOrWhiteSpace(evt.ScriptId) && !string.IsNullOrWhiteSpace(evt.ProposalId))
            next.LatestProposalByScript[evt.ScriptId] = evt.ProposalId;

        StampAppliedEvent(state, next, evt.ProposalId ?? string.Empty, "proposal_indexed");
        return next;
    }

    private static ScriptEvolutionManagerState ApplyStamped(
        ScriptEvolutionManagerState state,
        IMessage evt)
    {
        var next = state.Clone();
        var proposalId = evt switch
        {
            ScriptEvolutionRollbackRequestedEvent rollbackRequested => rollbackRequested.ProposalId ?? string.Empty,
            ScriptEvolutionRolledBackEvent rolledBack => rolledBack.ProposalId ?? string.Empty,
            _ => string.Empty,
        };
        var status = evt switch
        {
            ScriptEvolutionRollbackRequestedEvent => ScriptEvolutionStatuses.RollbackRequested,
            ScriptEvolutionRolledBackEvent => ScriptEvolutionStatuses.RolledBack,
            _ => "unknown",
        };
        StampAppliedEvent(state, next, proposalId, status);
        return next;
    }

    private static void StampAppliedEvent(
        ScriptEvolutionManagerState current,
        ScriptEvolutionManagerState next,
        string proposalId,
        string status)
    {
        next.LastAppliedEventVersion = current.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(proposalId, ":", status);
    }
}
