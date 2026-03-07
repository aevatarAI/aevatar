using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

public sealed class ScriptEvolutionSessionGAgent : GAgentBase<ScriptEvolutionSessionState>
{
    private const string SessionStatusStarted = "session_started";
    private const string StatusPromotionFailed = "promotion_failed";
    private const string PromotionFailedFailureReasonTag = "[promotion_failed]";

    private readonly IScriptEvolutionFlowPort _evolutionFlowPort;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public ScriptEvolutionSessionGAgent(
        IScriptEvolutionFlowPort evolutionFlowPort,
        IScriptingActorAddressResolver addressResolver)
    {
        _evolutionFlowPort = evolutionFlowPort ?? throw new ArgumentNullException(nameof(evolutionFlowPort));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleStartScriptEvolutionSessionRequested(StartScriptEvolutionSessionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var proposal = NormalizeProposal(evt);
        if (!string.IsNullOrWhiteSpace(State.ProposalId))
        {
            if (!string.Equals(State.ProposalId, proposal.ProposalId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Script evolution session actor '{Id}' is bound to proposal '{State.ProposalId}', but received '{proposal.ProposalId}'.");
            }

            return;
        }

        await PersistDomainEventAsync(new ScriptEvolutionSessionStartedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            BaseRevision = proposal.BaseRevision,
            CandidateRevision = proposal.CandidateRevision,
        });

        await TryIndexProposalAsync(proposal, CancellationToken.None);
        await ExecuteProposalAsync(proposal, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleQueryScriptEvolutionDecisionRequested(QueryScriptEvolutionDecisionRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return;

        if (string.IsNullOrWhiteSpace(evt.ProposalId) ||
            string.IsNullOrWhiteSpace(State.ProposalId) ||
            !string.Equals(State.ProposalId, evt.ProposalId, StringComparison.Ordinal))
        {
            await SendQueryResponseAsync(evt.ReplyStreamId, new ScriptEvolutionDecisionRespondedEvent
            {
                RequestId = evt.RequestId,
                Found = false,
                ProposalId = evt.ProposalId ?? string.Empty,
                FailureReason = $"Proposal `{evt.ProposalId}` not found.",
            });
            return;
        }

        if (!State.Completed)
        {
            await SendQueryResponseAsync(evt.ReplyStreamId, new ScriptEvolutionDecisionRespondedEvent
            {
                RequestId = evt.RequestId,
                Found = false,
                ProposalId = State.ProposalId ?? string.Empty,
                FailureReason = "Proposal decision not completed yet.",
            });
            return;
        }

        var response = new ScriptEvolutionDecisionRespondedEvent
        {
            RequestId = evt.RequestId,
            Found = true,
            Accepted = State.Accepted,
            ProposalId = State.ProposalId ?? string.Empty,
            ScriptId = State.ScriptId ?? string.Empty,
            BaseRevision = State.BaseRevision ?? string.Empty,
            CandidateRevision = State.CandidateRevision ?? string.Empty,
            Status = State.Status ?? string.Empty,
            FailureReason = State.FailureReason ?? string.Empty,
            DefinitionActorId = string.IsNullOrWhiteSpace(State.DefinitionActorId)
                ? _addressResolver.GetDefinitionActorId(State.ScriptId ?? string.Empty)
                : State.DefinitionActorId,
            CatalogActorId = string.IsNullOrWhiteSpace(State.CatalogActorId)
                ? _addressResolver.GetCatalogActorId()
                : State.CatalogActorId,
        };
        response.Diagnostics.Add(State.Diagnostics);
        await SendQueryResponseAsync(evt.ReplyStreamId, response);
    }

    protected override ScriptEvolutionSessionState TransitionState(
        ScriptEvolutionSessionState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptEvolutionSessionStartedEvent>(ApplySessionStarted)
            .On<ScriptEvolutionSessionCompletedEvent>(ApplySessionCompleted)
            .OrCurrent();

    private async Task ExecuteProposalAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        var defaultDefinitionActorId = _addressResolver.GetDefinitionActorId(proposal.ScriptId);
        var defaultCatalogActorId = _addressResolver.GetCatalogActorId();

        await PersistDomainEventAsync(new ScriptEvolutionProposedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            BaseRevision = proposal.BaseRevision,
            CandidateRevision = proposal.CandidateRevision,
            CandidateSourceHash = proposal.CandidateSourceHash,
            Reason = proposal.Reason,
        }, ct);

        await PersistDomainEventAsync(new ScriptEvolutionBuildRequestedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            CandidateRevision = proposal.CandidateRevision,
        }, ct);

        try
        {
            var flowResult = await _evolutionFlowPort.ExecuteAsync(proposal, ct);
            if (flowResult.Status == ScriptEvolutionFlowStatus.PolicyRejected)
            {
                await PersistRejectedAndCompletedAsync(
                    proposal,
                    proposal.CandidateRevision,
                    flowResult.FailureReason ?? string.Empty,
                    Array.Empty<string>(),
                    ScriptEvolutionStatuses.Rejected,
                    defaultDefinitionActorId,
                    defaultCatalogActorId,
                    ct);
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
            }, ct);

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
                }, ct);

                await PersistDomainEventAsync(BuildCompletedEvent(
                    accepted: true,
                    proposal,
                    status: ScriptEvolutionStatuses.Promoted,
                    failureReason: string.Empty,
                    definitionActorId: promotion.DefinitionActorId,
                    catalogActorId: promotedCatalogActorId,
                    diagnostics: validation.Diagnostics), ct);
                return;
            }

            if (flowResult.Status == ScriptEvolutionFlowStatus.PromotionFailed)
            {
                var failureReason = flowResult.FailureReason ?? string.Empty;
                var persistedFailureReason = TagPromotionFailedFailureReason(failureReason);
                var failurePromotion = flowResult.Promotion;

                await PersistDomainEventAsync(new ScriptEvolutionRejectedEvent
                {
                    ProposalId = proposal.ProposalId,
                    ScriptId = proposal.ScriptId,
                    CandidateRevision = proposal.CandidateRevision,
                    FailureReason = persistedFailureReason,
                }, ct);

                await PersistDomainEventAsync(BuildCompletedEvent(
                    accepted: false,
                    proposal,
                    status: StatusPromotionFailed,
                    failureReason: failureReason,
                    definitionActorId: string.IsNullOrWhiteSpace(failurePromotion?.DefinitionActorId)
                        ? defaultDefinitionActorId
                        : failurePromotion.DefinitionActorId,
                    catalogActorId: string.IsNullOrWhiteSpace(failurePromotion?.CatalogActorId)
                        ? defaultCatalogActorId
                        : failurePromotion.CatalogActorId,
                    diagnostics: validation.Diagnostics), ct);
                return;
            }

            await PersistRejectedAndCompletedAsync(
                proposal,
                proposal.CandidateRevision,
                flowResult.FailureReason ?? string.Empty,
                validation.Diagnostics,
                ScriptEvolutionStatuses.Rejected,
                defaultDefinitionActorId,
                defaultCatalogActorId,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await PersistRejectedAndCompletedAsync(
                proposal,
                proposal.CandidateRevision,
                ex.Message,
                Array.Empty<string>(),
                ScriptEvolutionStatuses.Rejected,
                defaultDefinitionActorId,
                defaultCatalogActorId,
                ct);
        }
    }

    private async Task PersistRejectedAndCompletedAsync(
        ScriptEvolutionProposal proposal,
        string candidateRevision,
        string failureReason,
        IReadOnlyList<string> diagnostics,
        string status,
        string definitionActorId,
        string catalogActorId,
        CancellationToken ct)
    {
        await PersistDomainEventAsync(new ScriptEvolutionRejectedEvent
        {
            ProposalId = proposal.ProposalId,
            ScriptId = proposal.ScriptId,
            CandidateRevision = candidateRevision,
            FailureReason = failureReason,
        }, ct);

        await PersistDomainEventAsync(BuildCompletedEvent(
            accepted: false,
            proposal,
            status,
            failureReason,
            definitionActorId,
            catalogActorId,
            diagnostics), ct);
    }

    private async Task TryIndexProposalAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        var managerActorId = _addressResolver.GetEvolutionManagerActorId();
        try
        {
            await SendToAsync(managerActorId, new ScriptEvolutionProposalIndexedEvent
            {
                ProposalId = proposal.ProposalId,
                ScriptId = proposal.ScriptId,
                SessionActorId = Id,
            }, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Failed to index script evolution proposal. session_actor_id={SessionActorId} proposal_id={ProposalId}",
                Id,
                proposal.ProposalId);
        }
    }

    private Task SendQueryResponseAsync(
        string replyStreamId,
        ScriptEvolutionDecisionRespondedEvent response,
        CancellationToken ct = default)
    {
        return EventPublisher.SendToAsync(replyStreamId, response, ct, sourceEnvelope: null);
    }

    private static ScriptEvolutionSessionCompletedEvent BuildCompletedEvent(
        bool accepted,
        ScriptEvolutionProposal proposal,
        string status,
        string failureReason,
        string definitionActorId,
        string catalogActorId,
        IReadOnlyList<string> diagnostics)
    {
        var completed = new ScriptEvolutionSessionCompletedEvent
        {
            ProposalId = proposal.ProposalId ?? string.Empty,
            Accepted = accepted,
            Status = status ?? string.Empty,
            FailureReason = failureReason ?? string.Empty,
            DefinitionActorId = definitionActorId ?? string.Empty,
            CatalogActorId = catalogActorId ?? string.Empty,
        };
        if (diagnostics is { Count: > 0 })
            completed.Diagnostics.Add(diagnostics);
        return completed;
    }

    private static ScriptEvolutionProposal NormalizeProposal(StartScriptEvolutionSessionRequestedEvent evt)
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

    private static ScriptEvolutionSessionState ApplySessionStarted(
        ScriptEvolutionSessionState state,
        ScriptEvolutionSessionStartedEvent evt)
    {
        var next = state.Clone();
        next.ProposalId = evt.ProposalId ?? string.Empty;
        next.ScriptId = evt.ScriptId ?? string.Empty;
        next.BaseRevision = evt.BaseRevision ?? string.Empty;
        next.CandidateRevision = evt.CandidateRevision ?? string.Empty;
        next.Completed = false;
        next.Accepted = false;
        next.Status = SessionStatusStarted;
        next.FailureReason = string.Empty;
        next.DefinitionActorId = string.Empty;
        next.CatalogActorId = string.Empty;
        next.Diagnostics.Clear();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(next.ProposalId, ":session-started");
        return next;
    }

    private static ScriptEvolutionSessionState ApplySessionCompleted(
        ScriptEvolutionSessionState state,
        ScriptEvolutionSessionCompletedEvent evt)
    {
        var next = state.Clone();
        next.Completed = true;
        next.Accepted = evt.Accepted;
        next.Status = evt.Status ?? string.Empty;
        next.FailureReason = evt.FailureReason ?? string.Empty;
        next.DefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        next.CatalogActorId = evt.CatalogActorId ?? string.Empty;
        next.Diagnostics.Clear();
        next.Diagnostics.Add(evt.Diagnostics);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(next.ProposalId, ":session-completed");
        return next;
    }

    private static string TagPromotionFailedFailureReason(string failureReason)
    {
        var normalized = failureReason ?? string.Empty;
        return normalized.StartsWith(PromotionFailedFailureReasonTag, StringComparison.Ordinal)
            ? normalized
            : PromotionFailedFailureReasonTag + normalized;
    }
}
