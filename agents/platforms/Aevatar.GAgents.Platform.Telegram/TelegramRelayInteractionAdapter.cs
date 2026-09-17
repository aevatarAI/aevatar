using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;

namespace Aevatar.GAgents.Platform.Telegram;

/// <summary>Preserves existing compact typed Relay submissions; does not claim native callback-button delivery.</summary>
public sealed class TelegramRelayInteractionAdapter : INyxIdRelayInteractionAdapter
{
    public string Platform => "telegram";
    public CardActionSubmission? NormalizeInteraction(NyxIdRelayCallbackPayload payload) =>
        NyxIdRelayCardActionParser.Parse(payload.Content?.Text?.Trim(), payload);
}
