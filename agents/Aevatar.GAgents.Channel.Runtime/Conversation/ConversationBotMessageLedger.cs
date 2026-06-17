using Google.Protobuf.Collections;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Pure helpers over a conversation's bounded ledger of platform message ids the bot has sent.
/// ConversationGAgent owns the ledger in its state; the channel runner's group-chat gate uses it
/// (via a precomputed <c>IsReplyToBot</c> flag) so a reply to one of the bot's own messages counts
/// as addressing the bot without a re-@-mention.
/// </summary>
public static class ConversationBotMessageLedger
{
    /// <summary>Upper bound on retained bot message ids — replies target recent messages, so a
    /// small ring is sufficient and keeps the actor state bounded.</summary>
    public const int MaxTrackedBotMessageIds = 64;

    public static void RecordBotSentMessageId(RepeatedField<string> ledger, string? platformMessageId)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        var id = platformMessageId?.Trim();
        if (string.IsNullOrEmpty(id) || ledger.Contains(id))
            return;

        ledger.Add(id);
        while (ledger.Count > MaxTrackedBotMessageIds)
            ledger.RemoveAt(0);
    }

    public static bool IsReplyToBotMessage(IEnumerable<string> ledger, string? replyToActivityId)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        var parentId = replyToActivityId?.Trim();
        return !string.IsNullOrEmpty(parentId) && ledger.Contains(parentId);
    }
}
