using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgents.NyxidChat;

public static class ChannelCardActionRouting
{
    private const string CardActionChatType = "card_action";

    public static bool TryBuildWorkflowResumeCommand(
        InboundMessage inbound,
        out WorkflowResumeCommand? command)
    {
        command = null;
        ArgumentNullException.ThrowIfNull(inbound);

        if (!string.Equals(inbound.ChatType, CardActionChatType, StringComparison.Ordinal))
            return false;

        if (!TryGetRequiredValue(inbound.Extra, "actor_id", out var actorId) ||
            !TryGetRequiredValue(inbound.Extra, "run_id", out var runId) ||
            !TryGetRequiredValue(inbound.Extra, "step_id", out var stepId))
        {
            return false;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.platform"] = inbound.Platform,
            ["channel.conversation_id"] = inbound.ConversationId,
        };
        if (!string.IsNullOrWhiteSpace(inbound.MessageId))
            metadata["channel.message_id"] = inbound.MessageId;

        var approved = ResolveApproved(inbound.Extra);
        var editedContent = ResolveEditedContent(inbound.Extra);
        var feedback = ResolveFeedback(inbound.Extra, approved);
        command = new WorkflowResumeCommand(
            actorId,
            runId,
            stepId,
            NormalizeOptional(inbound.MessageId),
            approved,
            ResolveUserInput(inbound.Extra, approved),
            metadata,
            editedContent,
            feedback);
        return true;
    }

    private static bool TryGetRequiredValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!values.TryGetValue(key, out var raw))
            return false;

        value = (raw ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool ResolveApproved(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("approved", out var rawApproved) &&
            bool.TryParse(rawApproved, out var approved))
        {
            return approved;
        }

        if (values.TryGetValue("action", out var rawAction))
            return ResolveDecisionLikeValue(rawAction);

        if (values.TryGetValue("decision", out var rawDecision))
            return ResolveDecisionLikeValue(rawDecision);

        return true;
    }

    private static bool ResolveDecisionLikeValue(string? raw)
    {
        var normalized = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "reject" or "rejected" or "deny" or "denied" or "cancel" => false,
            _ => true,
        };
    }

    private static string? ResolveUserInput(IReadOnlyDictionary<string, string> values, bool approved)
    {
        var preferredKeys = approved
            ? new[] { "edited_content", "user_input", "input", "comment" }
            : new[] { "user_input", "comment", "input", "edited_content" };

        foreach (var key in preferredKeys)
        {
            if (values.TryGetValue(key, out var raw))
            {
                var normalized = NormalizeOptional(raw);
                if (normalized is not null)
                    return normalized;
            }
        }

        return null;
    }

    private static string? ResolveEditedContent(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("edited_content", out var raw))
            return null;

        return NormalizeOptional(raw);
    }

    private static string? ResolveFeedback(IReadOnlyDictionary<string, string> values, bool approved)
    {
        if (approved)
        {
            if (values.TryGetValue("user_input", out var approvedRaw))
                return NormalizeOptional(approvedRaw);

            return null;
        }

        foreach (var key in new[] { "user_input", "comment", "input", "edited_content" })
        {
            if (values.TryGetValue(key, out var raw))
            {
                var normalized = NormalizeOptional(raw);
                if (normalized is not null)
                    return normalized;
            }
        }

        return null;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized;
    }
}
