using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

/// <summary>
/// Builds channel-neutral <see cref="MessageContent"/> payloads for the Day One agent builder flow.
/// Actions and CardBlocks let the platform composer render native interactive cards instead of
/// bouncing a pre-serialized JSON blob through a plain-text fallback.
/// </summary>
public static class AgentBuilderCardContent
{
    private const string ListAgentsAction = AgentBuilderActionIds.ListAgents;

    /// <summary>
    /// Renders <c>/agents</c> as a single consolidated card. The earlier design produced one
    /// <see cref="CardBlock"/> per agent plus per-agent "Status: …" buttons; in Lark that compiled
    /// into many stacked markdown blocks followed by a long button row, which users perceived as a
    /// text list mixed with a separate status card (issue #476). The unified design surfaces one
    /// card with a structured agent list in the body and a small footer of global actions, while
    /// per-agent operations stay accessible through the documented slash commands listed inline.
    /// </summary>
    /// <param name="root">The list-agents tool result JSON root element.</param>
    /// <param name="noticeMarkdown">
    /// Optional headline to prepend to the body, e.g. a "Deleted agent X" notice when the same
    /// renderer is reused as the post-delete acknowledgment so the user sees the updated registry
    /// without a second card hop.
    /// </param>
    public static MessageContent FormatListAgentsResult(JsonElement root, string? noticeMarkdown = null)
    {
        if (TryReadError(root, out var error))
            return TextContent($"List agents failed: {error}");

        var content = new MessageContent();
        var notice = NormalizeOptionalMarkdown(noticeMarkdown);

        if (!root.TryGetProperty("agents", out var agentsElement) ||
            agentsElement.ValueKind != JsonValueKind.Array ||
            agentsElement.GetArrayLength() == 0)
        {
            var emptyBody = new StringBuilder();
            if (notice is not null)
            {
                emptyBody.Append(notice);
                emptyBody.Append("\n\n");
            }
            emptyBody.Append("No agents yet.");

            content.Cards.Add(new CardBlock
            {
                Kind = CardBlockKind.Section,
                BlockId = "agents_empty",
                Title = "Your Agents",
                Text = emptyBody.ToString(),
            });
            content.Actions.Add(BuildAction("Refresh", ListAgentsAction, isPrimary: false));
            return content;
        }

        var totalCount = agentsElement.GetArrayLength();
        var bodyBuilder = new StringBuilder();
        if (notice is not null)
        {
            bodyBuilder.Append(notice);
            bodyBuilder.Append("\n\n");
        }

        var index = 0;
        foreach (var agent in agentsElement.EnumerateArray())
        {
            index++;
            var agentId = TryReadString(agent, "agent_id") ?? "unknown-agent";
            var template = TryReadString(agent, "template") ?? "unknown-template";
            var status = TryReadString(agent, "status") ?? "unknown";
            var nextRun = TryReadString(agent, "next_scheduled_run") ?? "pending";
            var lastRun = TryReadOptional(agent, "last_run_at");
            var outputFormat = TryReadString(agent, "output_format") ?? "auto";

            if (index > 1)
                bodyBuilder.Append("\n\n");

            bodyBuilder.Append($"**{index}. `{template}`** · {status}\n");
            bodyBuilder.Append($"- Agent ID: `{agentId}`\n");
            bodyBuilder.Append($"- Output: `{outputFormat}`\n");
            bodyBuilder.Append($"- Next run: `{nextRun}`");
            if (lastRun is not null)
            {
                bodyBuilder.Append('\n');
                bodyBuilder.Append($"- Last run: `{lastRun}`");
            }
        }

        bodyBuilder.Append("\n\n**Manage agents** with these commands:\n");
        bodyBuilder.Append("- `/agent-status <id>` — view full details\n");
        bodyBuilder.Append("- `/run-agent <id>` — trigger immediately\n");
        bodyBuilder.Append("- `/disable-agent <id>` · `/enable-agent <id>` — toggle scheduling\n");
        bodyBuilder.Append("- `/delete-agent <id> confirm` — remove the agent");

        content.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = "agents_list",
            Title = $"Your Agents ({totalCount})",
            Text = bodyBuilder.ToString(),
        });

        content.Actions.Add(BuildAction("Refresh", ListAgentsAction, isPrimary: false));
        return content;
    }

    private static string? NormalizeOptionalMarkdown(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static ActionElement BuildAction(string label, string agentBuilderAction, bool isPrimary)
    {
        var button = new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = agentBuilderAction,
            Label = label,
            IsPrimary = isPrimary,
        };
        button.Arguments["agent_builder_action"] = agentBuilderAction;
        return button;
    }

    private static MessageContent TextContent(string text) => AgentBuilderJson.TextContent(text);

    private static bool TryReadError(JsonElement root, out string error) =>
        AgentBuilderJson.TryReadError(root, out error);

    private static string? TryReadString(JsonElement element, string propertyName) =>
        AgentBuilderJson.TryReadString(element, propertyName);

    private static string? TryReadOptional(JsonElement element, string propertyName) =>
        AgentBuilderJson.TryReadOptional(element, propertyName);
}
