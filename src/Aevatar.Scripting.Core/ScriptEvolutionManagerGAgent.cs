using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed class ScriptEvolutionManagerGAgent : GAgentBase<ScriptEvolutionManagerState>
{
    private const string StatusPromotionFailed = "promotion_failed";
    private const string PromotionFailedFailureReasonTag = "[promotion_failed]";
    private readonly IScriptEvolutionFlowPort _evolutionFlowPort;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public ScriptEvolutionManagerGAgent(
        IScriptEvolutionFlowPort evolutionFlowPort,
        IScriptingActorAddressResolver addressResolver)
    {
        _evolutionFlowPort = evolutionFlowPort ?? throw new ArgumentNullException(nameof(evolutionFlowPort));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
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
        });

        await PersistDomainEventAsync(new ScriptEvolutionBuildRequestedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            CandidateRevision = proposal.CandidateRevision,
        });

        var flowResult = await _evolutionFlowPort.ExecuteAsync(proposal, CancellationToken.None);
        var defaultDefinitionActorId = _addressResolver.GetDefinitionActorId(proposal.ScriptId);
        var defaultCatalogActorId = _addressResolver.GetCatalogActorId();

        if (flowResult.Status == ScriptEvolutionFlowStatus.PolicyRejected)
        {
            await RejectAndRespondAsync(
                evt,
                proposal,
                proposal.CandidateRevision,
                flowResult.FailureReason ?? string.Empty,
                Array.Empty<string>(),
                defaultDefinitionActorId,
                defaultCatalogActorId);
            return;
        }

        var validation = flowResult.ValidationReport ?? ScriptEvolutionValidationReport.Empty;
        await PersistDomainEventAsync(new ScriptEvolutionValidatedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            CandidateRevision = proposal.CandidateRevision,
            IsValid = validation.IsSuccess,
            Diagnostics = { validation.Diagnostics },
        });

        if (flowResult.Status == ScriptEvolutionFlowStatus.Promoted)
        {
            var promotion = flowResult.Promotion
                ?? throw new InvalidOperationException("Promotion result is required when flow is promoted.");
            var promotedCatalogActorId = string.IsNullOrWhiteSpace(promotion.CatalogActorId)
                ? defaultCatalogActorId
                : promotion.CatalogActorId;

            await PersistDomainEventAsync(new ScriptEvolutionPromotedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                CandidateRevision = promotion.PromotedRevision,
                DefinitionActorId = promotion.DefinitionActorId,
                CatalogActorId = promotion.CatalogActorId,
            });
            await SendTerminalDecisionResponseAsync(
                evt,
                accepted: true,
                proposal,
                promotion.PromotedRevision,
                ScriptEvolutionStatuses.Promoted,
                string.Empty,
                promotion.DefinitionActorId,
                promotedCatalogActorId,
                validation.Diagnostics);
            return;
        }

        if (flowResult.Status == ScriptEvolutionFlowStatus.PromotionFailed)
        {
            var failureReason = flowResult.FailureReason ?? string.Empty;
            var persistedFailureReason = TagPromotionFailedFailureReason(failureReason);
            var fallbackDefinitionActorId = _addressResolver.GetDefinitionActorId(proposal.ScriptId);
            var fallbackCatalogActorId = _addressResolver.GetCatalogActorId();
            var failurePromotion = flowResult.Promotion;

            await PersistDomainEventAsync(new ScriptEvolutionRejectedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                CandidateRevision = proposal.CandidateRevision,
                FailureReason = persistedFailureReason,
            });
            await SendTerminalDecisionResponseAsync(
                evt,
                accepted: false,
                proposal,
                proposal.CandidateRevision,
                StatusPromotionFailed,
                failureReason,
                string.IsNullOrWhiteSpace(failurePromotion?.DefinitionActorId)
                    ? fallbackDefinitionActorId
                    : failurePromotion.DefinitionActorId,
                string.IsNullOrWhiteSpace(failurePromotion?.CatalogActorId)
                    ? fallbackCatalogActorId
                    : failurePromotion.CatalogActorId,
                validation.Diagnostics);
            return;
        }

        await RejectAndRespondAsync(
            evt,
            proposal,
            proposal.CandidateRevision,
            flowResult.FailureReason ?? string.Empty,
            validation.Diagnostics,
            defaultDefinitionActorId,
            defaultCatalogActorId);
    }

    [EventHandler]
    public async Task HandleScriptEvolutionRollbackRequested(ScriptEvolutionRollbackRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

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

    [EventHandler]
    public async Task HandleQueryScriptEvolutionDecisionRequested(QueryScriptEvolutionDecisionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return;

        if (string.IsNullOrWhiteSpace(evt.ProposalId))
        {
            await SendQueryResponseAsync(evt.ReplyStreamId, new ScriptEvolutionDecisionRespondedEvent
            {
                RequestId = evt.RequestId,
                Found = false,
                FailureReason = "ProposalId is required.",
            });
            return;
        }

        if (!State.Proposals.TryGetValue(evt.ProposalId, out var proposal))
        {
            await SendQueryResponseAsync(evt.ReplyStreamId, new ScriptEvolutionDecisionRespondedEvent
            {
                RequestId = evt.RequestId,
                Found = false,
                ProposalId = evt.ProposalId,
                FailureReason = $"Proposal `{evt.ProposalId}` not found.",
            });
            return;
        }

        var response = new ScriptEvolutionDecisionRespondedEvent
        {
            RequestId = evt.RequestId,
            Found = true,
            Accepted = string.Equals(proposal.Status, ScriptEvolutionStatuses.Promoted, StringComparison.Ordinal),
            ProposalId = proposal.ProposalId ?? string.Empty,
            ScriptId = proposal.ScriptId ?? string.Empty,
            BaseRevision = proposal.BaseRevision ?? string.Empty,
            CandidateRevision = proposal.CandidateRevision ?? string.Empty,
            Status = proposal.Status ?? string.Empty,
            FailureReason = proposal.FailureReason ?? string.Empty,
            DefinitionActorId = string.IsNullOrWhiteSpace(proposal.PromotedDefinitionActorId)
                ? _addressResolver.GetDefinitionActorId(proposal.ScriptId ?? string.Empty)
                : proposal.PromotedDefinitionActorId,
            CatalogActorId = _addressResolver.GetCatalogActorId(),
        };
        response.Diagnostics.Add(proposal.ValidationDiagnostics);
        await SendQueryResponseAsync(evt.ReplyStreamId, response);
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

    private Task SendQueryResponseAsync(
        string replyStreamId,
        ScriptEvolutionDecisionRespondedEvent response,
        CancellationToken ct = default)
    {
        return EventPublisher.SendToAsync(replyStreamId, response, ct, sourceEnvelope: null);
    }

    private Task SendTerminalDecisionResponseAsync(
        ProposeScriptEvolutionRequestedEvent request,
        bool accepted,
        ScriptEvolutionProposal proposal,
        string candidateRevision,
        string status,
        string failureReason,
        string definitionActorId,
        string catalogActorId,
        IReadOnlyList<string> diagnostics,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.CallbackActorId) ||
            string.IsNullOrWhiteSpace(request.CallbackRequestId))
        {
            return Task.CompletedTask;
        }

        var response = new ScriptEvolutionDecisionRespondedEvent
        {
            RequestId = request.CallbackRequestId,
            Found = true,
            Accepted = accepted,
            ProposalId = proposal.ProposalId ?? string.Empty,
            ScriptId = proposal.ScriptId ?? string.Empty,
            BaseRevision = proposal.BaseRevision ?? string.Empty,
            CandidateRevision = candidateRevision ?? string.Empty,
            Status = status ?? string.Empty,
            FailureReason = failureReason ?? string.Empty,
            DefinitionActorId = definitionActorId ?? string.Empty,
            CatalogActorId = catalogActorId ?? string.Empty,
        };
        if (diagnostics is { Count: > 0 })
            response.Diagnostics.Add(diagnostics);

        return SendToAsync(request.CallbackActorId, response, ct);
    }

    private async Task RejectAndRespondAsync(
        ProposeScriptEvolutionRequestedEvent request,
        ScriptEvolutionProposal proposal,
        string candidateRevision,
        string failureReason,
        IReadOnlyList<string> diagnostics,
        string definitionActorId,
        string catalogActorId,
        CancellationToken ct = default)
    {
        await PersistDomainEventAsync(new ScriptEvolutionRejectedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            CandidateRevision = candidateRevision,
            FailureReason = failureReason,
        });
        await SendTerminalDecisionResponseAsync(
            request,
            accepted: false,
            proposal,
            candidateRevision,
            ScriptEvolutionStatuses.Rejected,
            failureReason,
            definitionActorId,
            catalogActorId,
            diagnostics,
            ct);
    }

    private static ScriptEvolutionProposal NormalizeProposal(
        ProposeScriptEvolutionRequestedEvent evt)
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

        return new ScriptEvolutionProposal(
            ProposalId: proposalId,
            ScriptId: scriptId,
            BaseRevision: evt.BaseRevision ?? string.Empty,
            CandidateRevision: candidateRevision,
            CandidateSource: candidateSource,
            CandidateSourceHash: evt.CandidateSourceHash ?? string.Empty,
            Reason: evt.Reason ?? string.Empty);
    }

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
        ScriptEvolutionRejectedEvent evt)
    {
        var normalizedFailureReason = NormalizeFailureReason(evt.FailureReason, out var isPromotionFailed);
        return ApplyWithProposal(
            state,
            evt.ProposalId ?? string.Empty,
            evt.ScriptId ?? string.Empty,
            isPromotionFailed ? StatusPromotionFailed : ScriptEvolutionStatuses.Rejected,
            (_, proposal) => proposal.FailureReason = normalizedFailureReason);
    }

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

    private static string TagPromotionFailedFailureReason(string failureReason)
    {
        var normalized = failureReason ?? string.Empty;
        return normalized.StartsWith(PromotionFailedFailureReasonTag, StringComparison.Ordinal)
            ? normalized
            : PromotionFailedFailureReasonTag + normalized;
    }

    private static string NormalizeFailureReason(string? failureReason, out bool isPromotionFailed)
    {
        var normalized = failureReason ?? string.Empty;
        if (normalized.StartsWith(PromotionFailedFailureReasonTag, StringComparison.Ordinal))
        {
            isPromotionFailed = true;
            return normalized[PromotionFailedFailureReasonTag.Length..];
        }

        isPromotionFailed = false;
        return normalized;
    }
}
