using Aevatar.Scripting.Abstractions.Definitions;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptEvolutionSessionGAgent
{
    private async Task SendAcceptedResponseIfRequestedAsync(
        StartScriptEvolutionSessionRequestedEvent evt,
        string proposalId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return;

        await EventPublisher.SendToAsync(
            evt.ReplyStreamId,
            new ScriptEvolutionCommandAcceptedEvent
            {
                RequestId = evt.RequestId,
                Accepted = true,
                ProposalId = proposalId ?? string.Empty,
                ScriptId = string.IsNullOrWhiteSpace(State.ScriptId) ? evt.ScriptId ?? string.Empty : State.ScriptId,
                SessionActorId = Id,
            },
            ct,
            sourceEnvelope: null);
    }
}
