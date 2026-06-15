using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed class ScriptEvolutionManagerGAgent : GAgentBase<ScriptEvolutionManagerState>
{
    public ScriptEvolutionManagerGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public Task HandleScriptEvolutionProposed(ScriptEvolutionProposedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public Task HandleScriptEvolutionBuildRequested(ScriptEvolutionBuildRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public Task HandleScriptEvolutionValidated(ScriptEvolutionValidatedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public Task HandleScriptEvolutionRejected(ScriptEvolutionRejectedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public Task HandleScriptEvolutionPromoted(ScriptEvolutionPromotedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public Task HandleScriptEvolutionRollbackRequested(ScriptEvolutionRollbackRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public Task HandleScriptEvolutionRolledBack(ScriptEvolutionRolledBackEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventAsync(evt);
    }

    protected override ScriptEvolutionManagerState TransitionState(
        ScriptEvolutionManagerState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptEvolutionProposedEvent>(ApplyProposed)
            .On<ScriptEvolutionBuildRequestedEvent>(ApplyBuildRequested)
            .On<ScriptEvolutionValidatedEvent>(ApplyValidated)
            .On<ScriptEvolutionRejectedEvent>(ApplyRejected)
            .On<ScriptEvolutionPromotedEvent>(ApplyPromoted)
            .On<ScriptEvolutionRollbackRequestedEvent>(ApplyRollbackRequested)
            .On<ScriptEvolutionRolledBackEvent>(ApplyRolledBack)
            .OrCurrent();

    private static ScriptEvolutionManagerState ApplyProposed(
        ScriptEvolutionManagerState state,
        ScriptEvolutionProposedEvent evt) =>
        ApplyWithProposal(
            state,
            evt.ProposalId ?? string.Empty,
            evt.ScriptId ?? string.Empty,
            ScriptEvolutionStatuses.Proposed,
            (next, proposal) =>
            {
                proposal.ProposalId = evt.ProposalId ?? string.Empty;
                proposal.ScriptId = evt.ScriptId ?? string.Empty;
                proposal.BaseRevision = evt.BaseRevision ?? string.Empty;
                proposal.CandidateRevision = evt.CandidateRevision ?? string.Empty;
                proposal.CandidateSourceHash = evt.CandidateSourceHash ?? string.Empty;
                proposal.Reason = evt.Reason ?? string.Empty;
                proposal.PolicyAllowed = false;
                proposal.ValidationSucceeded = false;
                proposal.ValidationDiagnostics.Clear();
                proposal.FailureReason = string.Empty;
                proposal.PromotedDefinitionActorId = string.Empty;
                proposal.PromotedRevision = string.Empty;

                if (!string.IsNullOrWhiteSpace(proposal.ScriptId) && !string.IsNullOrWhiteSpace(proposal.ProposalId))
                    next.LatestProposalByScript[proposal.ScriptId] = proposal.ProposalId;
            });

    private static ScriptEvolutionManagerState ApplyBuildRequested(
        ScriptEvolutionManagerState state,
        ScriptEvolutionBuildRequestedEvent evt) =>
        ApplyWithProposal(
            state,
            evt.ProposalId ?? string.Empty,
            evt.ScriptId ?? string.Empty,
            ScriptEvolutionStatuses.BuildRequested,
            static (_, _) => { });

    private static ScriptEvolutionManagerState ApplyValidated(
        ScriptEvolutionManagerState state,
        ScriptEvolutionValidatedEvent evt)
    {
        var status = evt.IsValid
            ? ScriptEvolutionStatuses.Validated
            : ScriptEvolutionStatuses.ValidationFailed;
        return ApplyWithProposal(
            state,
            evt.ProposalId ?? string.Empty,
            evt.ScriptId ?? string.Empty,
            status,
            (_, proposal) =>
            {
                proposal.ValidationDiagnostics.Clear();
                proposal.ValidationDiagnostics.Add(evt.Diagnostics);
                proposal.ValidationSucceeded = evt.IsValid;
                proposal.PolicyAllowed = true;
            });
    }

    private static ScriptEvolutionManagerState ApplyRejected(
        ScriptEvolutionManagerState state,
        ScriptEvolutionRejectedEvent evt) =>
        ApplyWithProposal(
            state,
            evt.ProposalId ?? string.Empty,
            evt.ScriptId ?? string.Empty,
            string.IsNullOrWhiteSpace(evt.Status)
                ? ScriptEvolutionStatuses.Rejected
                : evt.Status,
            (_, proposal) => proposal.FailureReason = evt.FailureReason ?? string.Empty);

    private static ScriptEvolutionManagerState ApplyPromoted(
        ScriptEvolutionManagerState state,
        ScriptEvolutionPromotedEvent evt) =>
        ApplyWithProposal(
            state,
            evt.ProposalId ?? string.Empty,
            evt.ScriptId ?? string.Empty,
            ScriptEvolutionStatuses.Promoted,
            (_, proposal) =>
            {
                proposal.PromotedDefinitionActorId = evt.DefinitionActorId ?? string.Empty;
                proposal.PromotedRevision = evt.CandidateRevision ?? string.Empty;
                proposal.FailureReason = string.Empty;
            });

    private static ScriptEvolutionManagerState ApplyRollbackRequested(
        ScriptEvolutionManagerState state,
        ScriptEvolutionRollbackRequestedEvent evt) =>
        ApplyWithProposal(
            state,
            evt.ProposalId ?? string.Empty,
            evt.ScriptId ?? string.Empty,
            ScriptEvolutionStatuses.RollbackRequested,
            (_, proposal) =>
            {
                proposal.FailureReason = evt.Reason ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(evt.TargetRevision))
                    proposal.PromotedRevision = evt.TargetRevision;
            });

    private static ScriptEvolutionManagerState ApplyRolledBack(
        ScriptEvolutionManagerState state,
        ScriptEvolutionRolledBackEvent evt) =>
        ApplyWithProposal(
            state,
            evt.ProposalId ?? string.Empty,
            evt.ScriptId ?? string.Empty,
            ScriptEvolutionStatuses.RolledBack,
            (_, proposal) =>
            {
                proposal.PromotedRevision = evt.TargetRevision ?? string.Empty;
                proposal.FailureReason = string.Empty;
            });

    private static ScriptEvolutionManagerState ApplyWithProposal(
        ScriptEvolutionManagerState state,
        string proposalId,
        string scriptId,
        string status,
        Action<ScriptEvolutionManagerState, ScriptEvolutionProposalState> mutate)
    {
        var next = state.Clone();
        var proposal = GetOrCreateProposal(next, proposalId, scriptId);
        mutate(next, proposal);
        proposal.Status = status;
        StampAppliedEvent(state, next, proposal.ProposalId ?? string.Empty, status);
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

    private static ScriptEvolutionProposalState GetOrCreateProposal(
        ScriptEvolutionManagerState state,
        string proposalId,
        string scriptId)
    {
        var normalizedProposalId = proposalId ?? string.Empty;
        if (!state.Proposals.TryGetValue(normalizedProposalId, out var proposal))
        {
            proposal = new ScriptEvolutionProposalState
            {
                ProposalId = normalizedProposalId,
                ScriptId = scriptId ?? string.Empty,
            };
            state.Proposals[normalizedProposalId] = proposal;
        }

        return proposal;
    }
}
