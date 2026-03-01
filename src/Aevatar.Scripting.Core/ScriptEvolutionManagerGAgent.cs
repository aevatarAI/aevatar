using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed class ScriptEvolutionManagerGAgent : GAgentBase<ScriptEvolutionManagerState>, IScriptEvolutionDecisionSource
{
    private const string StatusProposed = "proposed";
    private const string StatusBuildRequested = "build_requested";
    private const string StatusValidated = "validated";
    private const string StatusValidationFailed = "validation_failed";
    private const string StatusRejected = "rejected";
    private const string StatusPromoted = "promoted";
    private const string StatusRollbackRequested = "rollback_requested";
    private const string StatusRolledBack = "rolled_back";

    private readonly IScriptPolicyGatePort _policyGatePort;
    private readonly IScriptValidationPipelinePort _validationPipelinePort;
    private readonly IScriptPromotionPort _promotionPort;

    public ScriptEvolutionManagerGAgent(
        IScriptPolicyGatePort policyGatePort,
        IScriptValidationPipelinePort validationPipelinePort,
        IScriptPromotionPort promotionPort)
    {
        _policyGatePort = policyGatePort ?? throw new ArgumentNullException(nameof(policyGatePort));
        _validationPipelinePort = validationPipelinePort ?? throw new ArgumentNullException(nameof(validationPipelinePort));
        _promotionPort = promotionPort ?? throw new ArgumentNullException(nameof(promotionPort));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleProposeScriptEvolutionRequested(ProposeScriptEvolutionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var proposal = NormalizeProposal(evt);
        await PersistDomainEventAsync(new ScriptEvolutionProposedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            BaseRevision = proposal.BaseRevision,
            CandidateRevision = proposal.CandidateRevision,
            CandidateSourceHash = proposal.CandidateSourceHash,
            Reason = proposal.Reason,
            DefinitionActorId = proposal.DefinitionActorId,
            CatalogActorId = proposal.CatalogActorId,
            RequestedByActorId = proposal.RequestedByActorId,
        });

        await PersistDomainEventAsync(new ScriptEvolutionBuildRequestedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            CandidateRevision = proposal.CandidateRevision,
        });

        var policyDecision = await _policyGatePort.EvaluateAsync(proposal, CancellationToken.None);
        if (!policyDecision.IsAllowed)
        {
            await PersistDomainEventAsync(new ScriptEvolutionRejectedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                CandidateRevision = proposal.CandidateRevision,
                FailureReason = string.IsNullOrWhiteSpace(policyDecision.FailureReason)
                    ? "Policy gate denied the proposal."
                    : policyDecision.FailureReason,
            });
            return;
        }

        var validation = await _validationPipelinePort.ValidateAsync(proposal, CancellationToken.None);
        await PersistDomainEventAsync(new ScriptEvolutionValidatedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            CandidateRevision = proposal.CandidateRevision,
            IsValid = validation.IsSuccess,
            Diagnostics = { validation.Diagnostics },
        });

        if (!validation.IsSuccess)
        {
            await PersistDomainEventAsync(new ScriptEvolutionRejectedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                CandidateRevision = proposal.CandidateRevision,
                FailureReason = string.Join("; ", validation.Diagnostics),
            });
            return;
        }

        try
        {
            var promotion = await _promotionPort.PromoteAsync(
                new ScriptPromotionRequest(
                    ProposalId: proposal.ProposalId,
                    ScriptId: proposal.ScriptId,
                    CandidateRevision: proposal.CandidateRevision,
                    CandidateSource: proposal.CandidateSource,
                    CandidateSourceHash: proposal.CandidateSourceHash,
                    DefinitionActorId: proposal.DefinitionActorId,
                    CatalogActorId: proposal.CatalogActorId),
                CancellationToken.None);

            await PersistDomainEventAsync(new ScriptEvolutionPromotedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                CandidateRevision = promotion.PromotedRevision,
                DefinitionActorId = promotion.DefinitionActorId,
                CatalogActorId = promotion.CatalogActorId,
            });
        }
        catch (Exception ex)
        {
            await PersistDomainEventAsync(new ScriptEvolutionRejectedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                CandidateRevision = proposal.CandidateRevision,
                FailureReason = ex.Message,
            });
        }
    }

    [EventHandler]
    public async Task HandleScriptEvolutionRollbackRequested(ScriptEvolutionRollbackRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        await _promotionPort.RollbackAsync(
            new ScriptRollbackRequest(
                ProposalId: evt.ProposalId ?? string.Empty,
                ScriptId: evt.ScriptId ?? string.Empty,
                TargetRevision: evt.TargetRevision ?? string.Empty,
                CatalogActorId: evt.CatalogActorId ?? string.Empty,
                Reason: evt.Reason ?? string.Empty),
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
            .On<ScriptEvolutionProposedEvent>(ApplyProposed)
            .On<ScriptEvolutionBuildRequestedEvent>(ApplyBuildRequested)
            .On<ScriptEvolutionValidatedEvent>(ApplyValidated)
            .On<ScriptEvolutionRejectedEvent>(ApplyRejected)
            .On<ScriptEvolutionPromotedEvent>(ApplyPromoted)
            .On<ScriptEvolutionRollbackRequestedEvent>(ApplyRollbackRequested)
            .On<ScriptEvolutionRolledBackEvent>(ApplyRolledBack)
            .OrCurrent();

    public ScriptPromotionDecision? GetDecision(string proposalId)
    {
        if (string.IsNullOrWhiteSpace(proposalId))
            return null;
        if (!State.Proposals.TryGetValue(proposalId, out var proposal))
            return null;

        var validation = new ScriptEvolutionValidationReport(
            IsSuccess: proposal.ValidationSucceeded,
            Diagnostics: proposal.ValidationDiagnostics.ToArray());
        var accepted = string.Equals(proposal.Status, StatusPromoted, StringComparison.Ordinal);

        return new ScriptPromotionDecision(
            Accepted: accepted,
            ProposalId: proposal.ProposalId ?? string.Empty,
            ScriptId: proposal.ScriptId ?? string.Empty,
            BaseRevision: proposal.BaseRevision ?? string.Empty,
            CandidateRevision: proposal.CandidateRevision ?? string.Empty,
            Status: proposal.Status ?? string.Empty,
            FailureReason: proposal.FailureReason ?? string.Empty,
            DefinitionActorId: string.IsNullOrWhiteSpace(proposal.PromotedDefinitionActorId)
                ? proposal.DefinitionActorId ?? string.Empty
                : proposal.PromotedDefinitionActorId,
            CatalogActorId: proposal.CatalogActorId ?? string.Empty,
            ValidationReport: validation);
    }

    private static ScriptEvolutionProposal NormalizeProposal(ProposeScriptEvolutionRequestedEvent evt)
    {
        var proposalId = string.IsNullOrWhiteSpace(evt.ProposalId)
            ? Guid.NewGuid().ToString("N")
            : evt.ProposalId;
        var scriptId = evt.ScriptId ?? string.Empty;
        var candidateRevision = evt.CandidateRevision ?? string.Empty;
        var candidateSource = evt.CandidateSource ?? string.Empty;

        if (string.IsNullOrWhiteSpace(scriptId))
            throw new InvalidOperationException("ScriptId is required.");
        if (string.IsNullOrWhiteSpace(candidateRevision))
            throw new InvalidOperationException("CandidateRevision is required.");
        if (string.IsNullOrWhiteSpace(candidateSource))
            throw new InvalidOperationException("CandidateSource is required.");

        var definitionActorId = string.IsNullOrWhiteSpace(evt.DefinitionActorId)
            ? $"script-definition:{scriptId}"
            : evt.DefinitionActorId;
        var catalogActorId = string.IsNullOrWhiteSpace(evt.CatalogActorId)
            ? "script-catalog"
            : evt.CatalogActorId;

        return new ScriptEvolutionProposal(
            ProposalId: proposalId,
            ScriptId: scriptId,
            BaseRevision: evt.BaseRevision ?? string.Empty,
            CandidateRevision: candidateRevision,
            CandidateSource: candidateSource,
            CandidateSourceHash: evt.CandidateSourceHash ?? string.Empty,
            Reason: evt.Reason ?? string.Empty,
            DefinitionActorId: definitionActorId,
            CatalogActorId: catalogActorId,
            RequestedByActorId: evt.RequestedByActorId ?? string.Empty);
    }

    private static ScriptEvolutionManagerState ApplyProposed(
        ScriptEvolutionManagerState state,
        ScriptEvolutionProposedEvent evt)
    {
        var next = state.Clone();
        var proposal = GetOrCreateProposal(next, evt.ProposalId ?? string.Empty, evt.ScriptId ?? string.Empty);

        proposal.ProposalId = evt.ProposalId ?? string.Empty;
        proposal.ScriptId = evt.ScriptId ?? string.Empty;
        proposal.BaseRevision = evt.BaseRevision ?? string.Empty;
        proposal.CandidateRevision = evt.CandidateRevision ?? string.Empty;
        proposal.CandidateSourceHash = evt.CandidateSourceHash ?? string.Empty;
        proposal.Reason = evt.Reason ?? string.Empty;
        proposal.DefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        proposal.CatalogActorId = evt.CatalogActorId ?? string.Empty;
        proposal.RequestedByActorId = evt.RequestedByActorId ?? string.Empty;
        proposal.PolicyAllowed = false;
        proposal.ValidationSucceeded = false;
        proposal.ValidationDiagnostics.Clear();
        proposal.FailureReason = string.Empty;
        proposal.PromotedDefinitionActorId = string.Empty;
        proposal.PromotedRevision = string.Empty;
        proposal.Status = StatusProposed;

        if (!string.IsNullOrWhiteSpace(proposal.ScriptId) && !string.IsNullOrWhiteSpace(proposal.ProposalId))
            next.LatestProposalByScript[proposal.ScriptId] = proposal.ProposalId;

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(proposal.ProposalId, ":", StatusProposed);
        return next;
    }

    private static ScriptEvolutionManagerState ApplyBuildRequested(
        ScriptEvolutionManagerState state,
        ScriptEvolutionBuildRequestedEvent evt)
    {
        var next = state.Clone();
        var proposal = GetOrCreateProposal(next, evt.ProposalId ?? string.Empty, evt.ScriptId ?? string.Empty);
        proposal.Status = StatusBuildRequested;

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(proposal.ProposalId, ":", StatusBuildRequested);
        return next;
    }

    private static ScriptEvolutionManagerState ApplyValidated(
        ScriptEvolutionManagerState state,
        ScriptEvolutionValidatedEvent evt)
    {
        var next = state.Clone();
        var proposal = GetOrCreateProposal(next, evt.ProposalId ?? string.Empty, evt.ScriptId ?? string.Empty);

        proposal.ValidationDiagnostics.Clear();
        proposal.ValidationDiagnostics.Add(evt.Diagnostics);
        proposal.ValidationSucceeded = evt.IsValid;
        proposal.PolicyAllowed = true;
        proposal.Status = evt.IsValid ? StatusValidated : StatusValidationFailed;

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(
            proposal.ProposalId,
            ":",
            evt.IsValid ? StatusValidated : StatusValidationFailed);
        return next;
    }

    private static ScriptEvolutionManagerState ApplyRejected(
        ScriptEvolutionManagerState state,
        ScriptEvolutionRejectedEvent evt)
    {
        var next = state.Clone();
        var proposal = GetOrCreateProposal(next, evt.ProposalId ?? string.Empty, evt.ScriptId ?? string.Empty);
        proposal.FailureReason = evt.FailureReason ?? string.Empty;
        proposal.Status = StatusRejected;

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(proposal.ProposalId, ":", StatusRejected);
        return next;
    }

    private static ScriptEvolutionManagerState ApplyPromoted(
        ScriptEvolutionManagerState state,
        ScriptEvolutionPromotedEvent evt)
    {
        var next = state.Clone();
        var proposal = GetOrCreateProposal(next, evt.ProposalId ?? string.Empty, evt.ScriptId ?? string.Empty);

        proposal.Status = StatusPromoted;
        proposal.PromotedDefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        proposal.PromotedRevision = evt.CandidateRevision ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.CatalogActorId))
            proposal.CatalogActorId = evt.CatalogActorId;

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(proposal.ProposalId, ":", StatusPromoted);
        return next;
    }

    private static ScriptEvolutionManagerState ApplyRollbackRequested(
        ScriptEvolutionManagerState state,
        ScriptEvolutionRollbackRequestedEvent evt)
    {
        var next = state.Clone();
        var proposal = GetOrCreateProposal(next, evt.ProposalId ?? string.Empty, evt.ScriptId ?? string.Empty);

        proposal.Status = StatusRollbackRequested;
        proposal.FailureReason = evt.Reason ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.TargetRevision))
            proposal.PromotedRevision = evt.TargetRevision;

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(proposal.ProposalId, ":", StatusRollbackRequested);
        return next;
    }

    private static ScriptEvolutionManagerState ApplyRolledBack(
        ScriptEvolutionManagerState state,
        ScriptEvolutionRolledBackEvent evt)
    {
        var next = state.Clone();
        var proposal = GetOrCreateProposal(next, evt.ProposalId ?? string.Empty, evt.ScriptId ?? string.Empty);

        proposal.Status = StatusRolledBack;
        proposal.PromotedRevision = evt.TargetRevision ?? string.Empty;
        proposal.FailureReason = string.Empty;

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(proposal.ProposalId, ":", StatusRolledBack);
        return next;
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
