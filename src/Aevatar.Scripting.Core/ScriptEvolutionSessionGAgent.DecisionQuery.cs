using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptEvolutionSessionGAgent
{
    private Task SendPendingDecisionResponseIfRequestedAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(State.PendingRequestId) ||
            string.IsNullOrWhiteSpace(State.PendingReplyStreamId))
        {
            return Task.CompletedTask;
        }

        return SendQueryResponseAsync(
            State.PendingReplyStreamId,
            BuildDecisionResponse(State.PendingRequestId, State.ProposalId ?? string.Empty),
            ct);
    }

    private async Task SendStartResponseIfRequestedAsync(
        StartScriptEvolutionSessionRequestedEvent evt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return;

        await SendQueryResponseAsync(
            evt.ReplyStreamId,
            BuildDecisionResponse(evt.RequestId, evt.ProposalId ?? string.Empty),
            ct);
    }

    private async Task HandleDecisionQueryAsync(
        QueryScriptEvolutionDecisionRequestedEvent evt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return;

        if (string.IsNullOrWhiteSpace(evt.ProposalId) ||
            string.IsNullOrWhiteSpace(State.ProposalId) ||
            !string.Equals(State.ProposalId, evt.ProposalId, StringComparison.Ordinal))
        {
            await SendQueryResponseAsync(
                evt.ReplyStreamId,
                BuildProposalNotFoundResponse(evt.RequestId, evt.ProposalId ?? string.Empty),
                ct);
            return;
        }

        await SendQueryResponseAsync(
            evt.ReplyStreamId,
            BuildDecisionResponse(evt.RequestId, State.ProposalId ?? string.Empty),
            ct);
    }

    private Task SendQueryResponseAsync(
        string replyStreamId,
        ScriptEvolutionDecisionRespondedEvent response,
        CancellationToken ct = default) =>
        EventPublisher.SendToAsync(replyStreamId, response, ct, sourceEnvelope: null);

    private ScriptEvolutionDecisionRespondedEvent BuildDecisionResponse(string requestId, string proposalId)
    {
        if (!string.IsNullOrWhiteSpace(proposalId) &&
            !string.IsNullOrWhiteSpace(State.ProposalId) &&
            !string.Equals(State.ProposalId, proposalId, StringComparison.Ordinal))
        {
            return BuildProposalNotFoundResponse(requestId, proposalId);
        }

        if (!State.Completed)
        {
            return new ScriptEvolutionDecisionRespondedEvent
            {
                RequestId = requestId,
                Found = false,
                ProposalId = string.IsNullOrWhiteSpace(State.ProposalId) ? proposalId : State.ProposalId,
                FailureReason = "Proposal decision not completed yet.",
            };
        }

        var response = new ScriptEvolutionDecisionRespondedEvent
        {
            RequestId = requestId,
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
        return response;
    }

    private static ScriptEvolutionDecisionRespondedEvent BuildProposalNotFoundResponse(
        string requestId,
        string proposalId) =>
        new()
        {
            RequestId = requestId,
            Found = false,
            ProposalId = proposalId,
            FailureReason = $"Proposal `{proposalId}` not found.",
        };
}
