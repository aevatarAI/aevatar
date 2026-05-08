using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.GAgents.Authoring.Lark;

public static class AgentBuilderCardFlow
{
    private const string PrivateChatType = "p2p";
    private const string CardActionChatType = "card_action";
    private const string ListAgentsAction = AgentBuilderActionIds.ListAgents;
    private const string AgentStatusAction = AgentBuilderActionIds.AgentStatus;
    private const string RunAgentAction = AgentBuilderActionIds.RunAgent;
    private const string DisableAgentAction = AgentBuilderActionIds.DisableAgent;
    private const string EnableAgentAction = AgentBuilderActionIds.EnableAgent;
    private const string ConfirmDeleteAgentAction = AgentBuilderActionIds.ConfirmDeleteAgent;
    private const string DeleteAgentAction = AgentBuilderActionIds.DeleteAgent;
    private const string AgentStatusCommand = "/agent-status";
    private const string RunAgentCommand = "/run-agent";
    private const string DisableAgentCommand = "/disable-agent";
    private const string EnableAgentCommand = "/enable-agent";
    private const string DeleteAgentCommand = "/delete-agent";

    private static readonly HashSet<string> ListIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        "/agents",
        "list agents",
        "我的助手",
    };

    public static bool TryResolve(ChannelInboundEvent evt, out AgentBuilderFlowDecision? decision) =>
        TryResolve(evt, preferredGithubUsername: null, out decision);

    public static Task<AgentBuilderFlowDecision?> TryResolveAsync(
        ChannelInboundEvent evt,
        IUserConfigQueryPort? userConfigQueryPort,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _ = userConfigQueryPort;
        _ = ct;

        TryResolve(evt, preferredGithubUsername: null, out var decision);
        return Task.FromResult(decision);
    }

    private static bool TryResolve(
        ChannelInboundEvent evt,
        string? preferredGithubUsername,
        out AgentBuilderFlowDecision? decision)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _ = preferredGithubUsername;
        decision = null;

        if (IsPrivateChatText(evt))
        {
            var normalized = NormalizeText(evt.Text);

            if (ListIntents.Contains(normalized))
            {
                decision = AgentBuilderFlowDecision.ToolCall(ListAgentsAction, """{"action":"list_agents"}""");
                return true;
            }

            if (TryResolvePrivateChatCommand(normalized, out decision))
                return true;

            return false;
        }

        if (!string.Equals(evt.ChatType, CardActionChatType, StringComparison.Ordinal))
            return false;

        if (!evt.Extra.TryGetValue("agent_builder_action", out var action))
            return false;

        string? argumentsJson;
        string? validationError;
        switch ((action ?? string.Empty).Trim())
        {
            case ListAgentsAction:
                decision = AgentBuilderFlowDecision.ToolCall(ListAgentsAction, """{"action":"list_agents"}""");
                return true;

            case AgentStatusAction:
                if (!TryBuildAgentActionArguments(evt, "agent_status", out argumentsJson, out validationError))
                {
                    decision = AgentBuilderFlowDecision.DirectReply(validationError!);
                    return true;
                }

                decision = AgentBuilderFlowDecision.ToolCall(AgentStatusAction, argumentsJson!);
                return true;

            case RunAgentAction:
                if (!TryBuildAgentActionArguments(evt, "run_agent", out argumentsJson, out validationError))
                {
                    decision = AgentBuilderFlowDecision.DirectReply(validationError!);
                    return true;
                }

                decision = AgentBuilderFlowDecision.ToolCall(RunAgentAction, argumentsJson!);
                return true;

            case DisableAgentAction:
                if (!TryBuildAgentActionArguments(evt, "disable_agent", out argumentsJson, out validationError))
                {
                    decision = AgentBuilderFlowDecision.DirectReply(validationError!);
                    return true;
                }

                decision = AgentBuilderFlowDecision.ToolCall(DisableAgentAction, argumentsJson!);
                return true;

            case EnableAgentAction:
                if (!TryBuildAgentActionArguments(evt, "enable_agent", out argumentsJson, out validationError))
                {
                    decision = AgentBuilderFlowDecision.DirectReply(validationError!);
                    return true;
                }

                decision = AgentBuilderFlowDecision.ToolCall(EnableAgentAction, argumentsJson!);
                return true;

            case ConfirmDeleteAgentAction:
                if (!TryGetRequiredExtra(evt, "agent_id", out var agentId))
                {
                    decision = AgentBuilderFlowDecision.DirectReply("agent_id is required for delete confirmation.");
                    return true;
                }

                decision = AgentBuilderFlowDecision.DirectReply(BuildDeleteConfirmationCard(
                    agentId,
                    evt.Extra.TryGetValue("template", out var template) ? template : null));
                return true;

            case DeleteAgentAction:
                if (!TryBuildAgentActionArguments(evt, "delete_agent", out argumentsJson, out validationError, confirm: true))
                {
                    decision = AgentBuilderFlowDecision.DirectReply(validationError!);
                    return true;
                }

                decision = AgentBuilderFlowDecision.ToolCall(DeleteAgentAction, argumentsJson!);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Formats the tool result for a card-action invocation. Each branch returns a structured
    /// <see cref="MessageContent"/> with <c>Cards</c> and <c>Actions</c> populated; never a Lark
    /// card JSON string wrapped as <see cref="MessageContent.Text"/>. The latter shape used to
    /// reach the relay verbatim and the user saw raw <c>{"config":...}</c> blobs (issue #482).
    /// </summary>
    public static MessageContent FormatToolResult(AgentBuilderFlowDecision decision, string toolResultJson)
    {
        ArgumentNullException.ThrowIfNull(decision);

        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            return decision.ToolAction switch
            {
                ListAgentsAction => AgentBuilderCardContent.FormatListAgentsResult(doc.RootElement),
                AgentStatusAction => FormatAgentStatusResult(doc.RootElement),
                RunAgentAction => FormatRunAgentResult(doc.RootElement),
                DisableAgentAction => FormatDisableAgentResult(doc.RootElement),
                EnableAgentAction => FormatEnableAgentResult(doc.RootElement),
                DeleteAgentAction => FormatDeleteAgentResultAsList(doc.RootElement),
                _ => ToTextContent(toolResultJson),
            };
        }
        catch (JsonException)
        {
            return ToTextContent(toolResultJson);
        }
    }

    private static MessageContent ToTextContent(string text) => AgentBuilderJson.TextContent(text);

    public static string ResolveToolChatType(ChannelInboundEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        return string.Equals(evt.ChatType, CardActionChatType, StringComparison.Ordinal)
            ? PrivateChatType
            : evt.ChatType;
    }

    private static bool TryBuildAgentActionArguments(
        ChannelInboundEvent evt,
        string action,
        out string? argumentsJson,
        out string? validationError,
        bool confirm = false)
    {
        argumentsJson = null;
        validationError = null;

        if (!TryGetRequiredExtra(evt, "agent_id", out var agentId))
        {
            validationError = "agent_id is required. Send /agents and retry from the latest card.";
            return false;
        }

        var revisionFeedback = string.Equals(action, "run_agent", StringComparison.Ordinal)
            ? NormalizeOptional(evt.Extra.TryGetValue("revision_feedback", out var rawRevisionFeedback)
                ? rawRevisionFeedback
                : (evt.Extra.TryGetValue("user_input", out var rawUserInput) ? rawUserInput : null))
            : null;

        argumentsJson = JsonSerializer.Serialize(new
        {
            action,
            agent_id = agentId,
            confirm,
            revision_feedback = revisionFeedback,
        });
        return true;
    }

    private static bool TryResolvePrivateChatCommand(
        string normalizedText,
        out AgentBuilderFlowDecision? decision)
    {
        decision = null;

        if (TryParseAgentCommand(normalizedText, AgentStatusCommand, out var agentId, out var errorReply))
        {
            if (errorReply != null)
            {
                decision = AgentBuilderFlowDecision.DirectReply(errorReply);
                return true;
            }

            decision = AgentBuilderFlowDecision.ToolCall(
                AgentStatusAction,
                JsonSerializer.Serialize(new
                {
                    action = AgentStatusAction,
                    agent_id = agentId,
                }));
            return true;
        }

        if (TryParseAgentCommand(normalizedText, RunAgentCommand, out agentId, out errorReply))
        {
            if (errorReply != null)
            {
                decision = AgentBuilderFlowDecision.DirectReply(errorReply);
                return true;
            }

            decision = AgentBuilderFlowDecision.ToolCall(
                RunAgentAction,
                JsonSerializer.Serialize(new
                {
                    action = RunAgentAction,
                    agent_id = agentId,
            }));
            return true;
        }

        if (TryParseAgentCommand(normalizedText, DisableAgentCommand, out agentId, out errorReply))
        {
            if (errorReply != null)
            {
                decision = AgentBuilderFlowDecision.DirectReply(errorReply);
                return true;
            }

            decision = AgentBuilderFlowDecision.ToolCall(
                DisableAgentAction,
                JsonSerializer.Serialize(new
                {
                    action = DisableAgentAction,
                    agent_id = agentId,
                }));
            return true;
        }

        if (TryParseAgentCommand(normalizedText, EnableAgentCommand, out agentId, out errorReply))
        {
            if (errorReply != null)
            {
                decision = AgentBuilderFlowDecision.DirectReply(errorReply);
                return true;
            }

            decision = AgentBuilderFlowDecision.ToolCall(
                EnableAgentAction,
                JsonSerializer.Serialize(new
                {
                    action = EnableAgentAction,
                    agent_id = agentId,
                }));
            return true;
        }

        if (TryResolveDeleteAgentTextCommand(normalizedText, out decision))
            return true;

        return false;
    }

    /// <summary>
    /// Parses <c>/delete-agent &lt;agent_id&gt; [confirm]</c>. The optional <c>confirm</c> trailer
    /// matches the NyxRelay text contract (and the inline command hint surfaced from the shared
    /// <c>/agents</c> renderer) so a user who follows the printed hint
    /// <c>/delete-agent &lt;id&gt; confirm</c> in a direct-webhook chat does not end up with
    /// <c>"&lt;id&gt; confirm"</c> being treated as a single agent_id by the legacy
    /// <see cref="TryParseAgentCommand"/> parser. Without the trailing keyword we still surface
    /// the explicit confirmation card; with it we skip the extra step and dispatch the delete
    /// directly, mirroring the relay path's semantics.
    /// </summary>
    private static bool TryResolveDeleteAgentTextCommand(
        string normalizedText,
        out AgentBuilderFlowDecision? decision)
    {
        decision = null;
        if (!normalizedText.StartsWith(DeleteAgentCommand, StringComparison.OrdinalIgnoreCase))
            return false;

        var tokens = ChannelTextCommandParser.Tokenize(normalizedText);
        if (tokens.Count < 2 || string.IsNullOrWhiteSpace(tokens[1]))
        {
            decision = AgentBuilderFlowDecision.DirectReply($"Usage: {DeleteAgentCommand} <agent_id>");
            return true;
        }

        var agentId = tokens[1].Trim();
        var confirmed = tokens.Count > 2 &&
                        string.Equals(tokens[2], "confirm", StringComparison.OrdinalIgnoreCase);

        if (confirmed)
        {
            decision = AgentBuilderFlowDecision.ToolCall(
                DeleteAgentAction,
                JsonSerializer.Serialize(new
                {
                    action = DeleteAgentAction,
                    agent_id = agentId,
                    confirm = true,
                }));
            return true;
        }

        decision = AgentBuilderFlowDecision.DirectReply(BuildDeleteConfirmationCard(agentId, null));
        return true;
    }

    private static bool TryParseAgentCommand(
        string normalizedText,
        string command,
        out string? agentId,
        out string? errorReply)
    {
        agentId = null;
        errorReply = null;

        if (!normalizedText.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            return false;

        var rawArgument = normalizedText.Length == command.Length
            ? string.Empty
            : normalizedText.Substring(command.Length).Trim();

        if (string.IsNullOrWhiteSpace(rawArgument))
        {
            errorReply = $"Usage: {command} <agent_id>";
            return true;
        }

        agentId = rawArgument;
        return true;
    }

    private static bool TryGetRequiredExtra(ChannelInboundEvent evt, string key, out string value)
    {
        value = string.Empty;
        if (!evt.Extra.TryGetValue(key, out var raw))
            return false;

        value = NormalizeOptional(raw) ?? string.Empty;
        return value.Length > 0;
    }

    private static bool IsPrivateChatText(ChannelInboundEvent evt) =>
        string.Equals(evt.ChatType, PrivateChatType, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(evt.Text);

    private static string NormalizeText(string? text) => (text ?? string.Empty).Trim();

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static MessageContent FormatAgentStatusResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return ToTextContent($"Agent status failed: {error}");

        var agentId = ReadString(root, "agent_id") ?? "unknown-agent";
        var template = ReadString(root, "template") ?? "unknown-template";
        var status = ReadString(root, "status") ?? "unknown";
        var scheduleCron = ReadString(root, "schedule_cron") ?? "n/a";
        var scheduleTimezone = ReadString(root, "schedule_timezone") ?? "n/a";
        var lastRunAt = ReadString(root, "last_run_at") ?? "n/a";
        var nextRunAt = ReadString(root, "next_scheduled_run") ?? "n/a";
        var errorCount = ReadString(root, "error_count") ?? "0";
        var lastError = NormalizeOptional(ReadString(root, "last_error"));
        var note = NormalizeOptional(ReadString(root, "note"));

        var body = new StringBuilder();
        body.Append($"- Agent ID: `{agentId}`\n");
        body.Append($"- Template: `{template}`\n");
        body.Append($"- Status: `{status}`\n");
        body.Append($"- Schedule: `{scheduleCron}` ({scheduleTimezone})\n");
        body.Append($"- Last run: `{lastRunAt}`\n");
        body.Append($"- Next run: `{nextRunAt}`\n");
        body.Append($"- Error count: `{errorCount}`");
        if (lastError is not null)
            body.Append($"\n- Last error: {lastError}");
        if (note is not null)
            body.Append("\n\n").Append(note);

        var content = new MessageContent();
        content.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = $"agent_status:{agentId}",
            Title = "Agent Status",
            Text = body.ToString(),
        });

        var isDisabled = string.Equals(
            status,
            SkillRunnerDefaults.StatusDisabled,
            StringComparison.OrdinalIgnoreCase);
        content.Actions.Add(BuildAgentScopedCardAction("Refresh Status", AgentStatusAction, agentId, isPrimary: false));
        if (isDisabled)
        {
            content.Actions.Add(BuildAgentScopedCardAction("Enable", EnableAgentAction, agentId, isPrimary: true));
        }
        else
        {
            content.Actions.Add(BuildAgentScopedCardAction("Run Now", RunAgentAction, agentId, isPrimary: true));
            content.Actions.Add(BuildAgentScopedCardAction("Disable", DisableAgentAction, agentId, isPrimary: false));
        }
        content.Actions.Add(BuildCardAction("Back to Agents", ListAgentsAction, isPrimary: false));

        var deleteButton = BuildAgentScopedCardAction("Delete", ConfirmDeleteAgentAction, agentId, isPrimary: false);
        deleteButton.IsDanger = true;
        deleteButton.Arguments["template"] = template;
        content.Actions.Add(deleteButton);
        return content;
    }

    private static MessageContent FormatRunAgentResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return ToTextContent($"Run agent failed: {error}");

        var agentId = ReadString(root, "agent_id") ?? "unknown-agent";
        var template = ReadString(root, "template") ?? "unknown-template";
        var note = ReadString(root, "note") ?? "Manual run dispatched.";

        var content = new MessageContent();
        content.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = $"run_triggered:{agentId}",
            Title = "Run Triggered",
            Text = $"Agent `{agentId}` (`{template}`)\n\n{note}",
        });
        content.Actions.Add(BuildCardAction("Back to Agents", ListAgentsAction, isPrimary: false));
        content.Actions.Add(BuildAgentScopedCardAction("Refresh Status", AgentStatusAction, agentId, isPrimary: true));
        return content;
    }

    private static MessageContent FormatDisableAgentResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return ToTextContent($"Disable agent failed: {error}");

        return FormatAgentStatusResult(root);
    }

    private static MessageContent FormatEnableAgentResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return ToTextContent($"Enable agent failed: {error}");

        return FormatAgentStatusResult(root);
    }

    private static MessageContent FormatDeleteAgentResultAsList(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return ToTextContent($"Delete agent failed: {error}");

        var status = ReadString(root, "status") ?? "accepted";
        var agentId = ReadString(root, "agent_id") ?? "unknown-agent";
        var revokedApiKeyId = ReadString(root, "revoked_api_key_id") ?? "n/a";
        var deleteNotice = ReadString(root, "delete_notice");
        var note = ReadString(root, "note");
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(deleteNotice))
            lines.Add(deleteNotice!);
        else
            lines.Add(
                string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase)
                    ? $"Deleted agent `{agentId}`. Revoked API key: `{revokedApiKeyId}`."
                    : $"Delete accepted for `{agentId}`. Revoked API key: `{revokedApiKeyId}`.");

        if (!string.IsNullOrWhiteSpace(note))
            lines.Add(note!);

        return AgentBuilderCardContent.FormatListAgentsResult(root, string.Join("\n", lines));
    }

    private static bool TryReadError(JsonElement root, out string error) =>
        AgentBuilderJson.TryReadError(root, out error);

    private static string? ReadString(JsonElement element, string propertyName) =>
        AgentBuilderJson.TryReadString(element, propertyName);

    private static MessageContent BuildDeleteConfirmationCard(string agentId, string? template)
    {
        var templateLabel = NormalizeOptional(template) ?? "unknown-template";
        var content = new MessageContent();
        content.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = $"delete_confirm:{agentId}",
            Title = "Delete Agent",
            Text =
                $"Delete agent `{agentId}` from template `{templateLabel}`?\n\n" +
                "This will disable scheduling, revoke the Nyx API key, and tombstone the registry entry.",
        });
        var confirmButton = BuildAgentScopedCardAction("Confirm Delete", DeleteAgentAction, agentId, isPrimary: false);
        confirmButton.IsDanger = true;
        content.Actions.Add(confirmButton);
        content.Actions.Add(BuildCardAction("Back to Agents", ListAgentsAction, isPrimary: false));
        return content;
    }

    private static ActionElement BuildCardAction(string label, string agentBuilderAction, bool isPrimary)
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

    private static ActionElement BuildAgentScopedCardAction(
        string label,
        string agentBuilderAction,
        string agentId,
        bool isPrimary)
    {
        var button = BuildCardAction(label, agentBuilderAction, isPrimary);
        button.Arguments["agent_id"] = agentId;
        return button;
    }

}

public sealed record AgentBuilderFlowDecision(
    bool RequiresToolExecution,
    string ReplyPayload,
    string? ToolArgumentsJson,
    string? ToolAction,
    MessageContent? ReplyContent = null)
{
    public static AgentBuilderFlowDecision DirectReply(string replyPayload) =>
        new(false, replyPayload, null, null);

    public static AgentBuilderFlowDecision DirectReply(MessageContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new AgentBuilderFlowDecision(
            RequiresToolExecution: false,
            ReplyPayload: string.IsNullOrWhiteSpace(content.Text) ? string.Empty : content.Text,
            ToolArgumentsJson: null,
            ToolAction: null,
            ReplyContent: content);
    }

    public static AgentBuilderFlowDecision ToolCall(string toolAction, string argumentsJson) =>
        new(true, string.Empty, argumentsJson, toolAction);
}
