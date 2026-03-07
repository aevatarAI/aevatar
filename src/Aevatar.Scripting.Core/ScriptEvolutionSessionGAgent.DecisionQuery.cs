using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptEvolutionSessionGAgent
{
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
            await SendQueryResponseAsync(evt.ReplyStreamId, new ScriptEvolutionDecisionRespondedEvent
            {
                RequestId = evt.RequestId,
                Found = false,
                ProposalId = evt.ProposalId ?? string.Empty,
                FailureReason = $"Proposal `{evt.ProposalId}` not found.",
            }, ct);
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
            }, ct);
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
        await SendQueryResponseAsync(evt.ReplyStreamId, response, ct);
    }

    private Task SendQueryResponseAsync(
        string replyStreamId,
        ScriptEvolutionDecisionRespondedEvent response,
        CancellationToken ct = default) =>
        EventPublisher.SendToAsync(replyStreamId, response, ct, sourceEnvelope: null);
}
