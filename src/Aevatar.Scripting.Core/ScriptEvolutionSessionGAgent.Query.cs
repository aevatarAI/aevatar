using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptEvolutionSessionGAgent
{
    [EventHandler]
    public async Task HandleQueryScriptEvolutionProposalSnapshotRequested(QueryScriptEvolutionProposalSnapshotRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return;

        if (string.IsNullOrWhiteSpace(State.ProposalId))
        {
            await SendEvolutionSnapshotQueryResponseAsync(
                evt.ReplyStreamId,
                new ScriptEvolutionProposalSnapshotRespondedEvent
                {
                    RequestId = evt.RequestId,
                    Found = false,
                    FailureReason = $"Script evolution session `{Id}` has not been initialized.",
                });
            return;
        }

        if (!string.IsNullOrWhiteSpace(evt.ProposalId) &&
            !string.Equals(evt.ProposalId, State.ProposalId, StringComparison.Ordinal))
        {
            await SendEvolutionSnapshotQueryResponseAsync(
                evt.ReplyStreamId,
                new ScriptEvolutionProposalSnapshotRespondedEvent
                {
                    RequestId = evt.RequestId,
                    Found = false,
                    ProposalId = evt.ProposalId,
                    FailureReason = $"Script evolution session `{Id}` is bound to proposal `{State.ProposalId}`.",
                });
            return;
        }

        var responded = new ScriptEvolutionProposalSnapshotRespondedEvent
        {
            RequestId = evt.RequestId,
            Found = true,
            ProposalId = State.ProposalId ?? string.Empty,
            ScriptId = State.ScriptId ?? string.Empty,
            BaseRevision = State.BaseRevision ?? string.Empty,
            CandidateRevision = State.CandidateRevision ?? string.Empty,
            Completed = State.Completed,
            Accepted = State.Accepted,
            Status = State.Status ?? string.Empty,
            FailureReason = State.FailureReason ?? string.Empty,
            DefinitionActorId = State.DefinitionActorId ?? string.Empty,
            CatalogActorId = State.CatalogActorId ?? string.Empty,
        };
        responded.Diagnostics.Add(State.Diagnostics);
        await SendEvolutionSnapshotQueryResponseAsync(evt.ReplyStreamId, responded);
    }

    private Task SendEvolutionSnapshotQueryResponseAsync(
        string replyStreamId,
        ScriptEvolutionProposalSnapshotRespondedEvent response,
        CancellationToken ct = default)
    {
        return EventPublisher.SendToAsync(replyStreamId, response, ct, sourceEnvelope: null);
    }
}
