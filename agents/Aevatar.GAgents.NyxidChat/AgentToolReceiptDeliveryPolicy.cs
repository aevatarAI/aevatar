using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat;

internal sealed record AgentToolReceiptDelivery(
    string ReplyText,
    MessageContent? OutboundIntent,
    IReadOnlyList<ConversationHistoryEntry> AppendedHistory);

internal static class AgentToolReceiptDeliveryPolicy
{
    private readonly record struct ReceiptIdentity(string CallId, string ToolName);

    internal const string UnverifiedMutationFallback =
        "The requested external action was not confirmed by a successful tool receipt.";

    internal static AgentToolReceiptDelivery Build(
        string? replyText,
        MessageContent? outboundIntent,
        IReadOnlyList<ConversationHistoryEntry>? appendedHistory,
        IReadOnlyList<AgentToolReceipt>? receipts,
        IReadOnlyList<AgentRunToolCall>? toolCalls,
        IAgentToolReceiptRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        var baseReplyText = replyText ?? string.Empty;
        var history = appendedHistory ?? [];
        if (receipts is not { Count: > 0 })
            return new AgentToolReceiptDelivery(baseReplyText, outboundIntent, history);

        var reconciled = Reconcile(receipts);
        var renderedReceipts = renderer.Render(reconciled, toolCalls ?? []).Trim();
        if (HasBlockingMutation(reconciled))
        {
            var deterministicText = string.IsNullOrWhiteSpace(renderedReceipts)
                ? UnverifiedMutationFallback
                : renderedReceipts;
            return new AgentToolReceiptDelivery(
                deterministicText,
                new MessageContent { Text = deterministicText },
                ReplaceBlockingAssistantNarratives(history, reconciled, deterministicText));
        }

        if (string.IsNullOrWhiteSpace(renderedReceipts))
            return new AgentToolReceiptDelivery(baseReplyText, outboundIntent, history);

        var renderedReplyText = AppendReceiptText(baseReplyText, renderedReceipts);
        var renderedOutboundIntent = outboundIntent?.Clone();
        if (renderedOutboundIntent is not null)
        {
            renderedOutboundIntent.Text = AppendReceiptText(
                renderedOutboundIntent.Text,
                renderedReceipts);
        }

        return new AgentToolReceiptDelivery(
            renderedReplyText,
            renderedOutboundIntent,
            UpdateFinalAssistantText(history, renderedReplyText));
    }

    internal static IReadOnlyList<AgentToolReceipt> Reconcile(IReadOnlyList<AgentToolReceipt> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        if (receipts.Count < 2)
            return receipts;

        var lastIndexByCall = new Dictionary<ReceiptIdentity, int>();
        for (var index = 0; index < receipts.Count; index++)
        {
            var identity = TryGetIdentity(receipts[index]);
            if (identity is not null)
                lastIndexByCall[identity.Value] = index;
        }

        var reconciled = new List<AgentToolReceipt>(receipts.Count);
        for (var index = 0; index < receipts.Count; index++)
        {
            var receipt = receipts[index];
            var identity = TryGetIdentity(receipt);
            if (identity is null || lastIndexByCall[identity.Value] == index)
                reconciled.Add(receipt);
        }

        return reconciled;
    }

    internal static bool HasBlockingMutation(IReadOnlyList<AgentToolReceipt> receipts) =>
        receipts.Any(IsBlockingMutation);

    private static bool IsBlockingMutation(AgentToolReceipt receipt) =>
        AgentToolReceiptEffectPolicy.IsMutatingOrLegacyEffectCapable(receipt) &&
        receipt.Status is AgentToolReceiptStatus.Error or
            AgentToolReceiptStatus.ApprovalRequired or
            AgentToolReceiptStatus.Denied or
            AgentToolReceiptStatus.AuthorizationRequired or
            AgentToolReceiptStatus.Unspecified;

    private static IReadOnlyList<ConversationHistoryEntry> ReplaceBlockingAssistantNarratives(
        IReadOnlyList<ConversationHistoryEntry> source,
        IReadOnlyList<AgentToolReceipt> receipts,
        string deterministicText)
    {
        var history = source.Select(static entry => entry.Clone()).ToList();
        var blockingReceipts = receipts.Where(IsBlockingMutation).ToArray();
        var hasUnidentifiedCall = blockingReceipts.Any(static receipt =>
            Normalize(receipt.CallId) is null);
        var blockingCalls = blockingReceipts
            .Select(TryGetIdentity)
            .Where(static identity => identity is not null)
            .Select(static identity => identity!.Value)
            .ToHashSet();

        foreach (var entry in history)
        {
            if (!string.Equals(entry.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                entry.ToolCalls.Count == 0 ||
                (!hasUnidentifiedCall && !entry.ToolCalls.Any(call =>
                    TryGetIdentity(call) is { } identity && blockingCalls.Contains(identity))))
            {
                continue;
            }

            ClearNarrative(entry);
        }

        var narrativeIndex = history.FindLastIndex(static entry =>
            string.Equals(entry.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
            entry.ToolCalls.Count == 0);
        if (narrativeIndex < 0)
        {
            history.Add(new ConversationHistoryEntry
            {
                Role = "assistant",
                Content = deterministicText,
            });
            return history;
        }

        var narrative = history[narrativeIndex];
        ClearNarrative(narrative);
        narrative.Content = deterministicText;
        narrative.ToolCallId = string.Empty;
        return history;
    }

    private static void ClearNarrative(ConversationHistoryEntry entry)
    {
        entry.Content = string.Empty;
        entry.ReasoningContent = string.Empty;
        entry.ContentParts.Clear();
    }

    private static IReadOnlyList<ConversationHistoryEntry> UpdateFinalAssistantText(
        IReadOnlyList<ConversationHistoryEntry> source,
        string replyText)
    {
        var history = source.Select(static entry => entry.Clone()).ToList();
        var narrativeIndex = history.FindLastIndex(static entry =>
            string.Equals(entry.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
            entry.ToolCalls.Count == 0);
        if (narrativeIndex < 0)
        {
            history.Add(new ConversationHistoryEntry
            {
                Role = "assistant",
                Content = replyText,
            });
            return history;
        }

        history[narrativeIndex].Content = replyText;
        return history;
    }

    private static string AppendReceiptText(string? text, string renderedReceipts) =>
        string.IsNullOrWhiteSpace(text)
            ? renderedReceipts
            : $"{text.TrimEnd()}\n\n{renderedReceipts}";

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ReceiptIdentity? TryGetIdentity(AgentToolReceipt receipt)
    {
        var callId = Normalize(receipt.CallId);
        return callId is null
            ? null
            : new ReceiptIdentity(callId, Normalize(receipt.ToolName) ?? string.Empty);
    }

    private static ReceiptIdentity? TryGetIdentity(ConversationToolCallEntry call)
    {
        var callId = Normalize(call.Id);
        return callId is null
            ? null
            : new ReceiptIdentity(callId, Normalize(call.Name) ?? string.Empty);
    }
}
