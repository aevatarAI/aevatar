using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.Authoring.Lark;

public static class NyxRelayAgentBuilderFlow
{
    private const string PrivateChatType = "p2p";
    private const string ListAgentsCommand = "/agents";
    private const string AgentStatusCommand = "/agent-status";
    private const string RunAgentCommand = "/run-agent";
    private const string DisableAgentCommand = "/disable-agent";
    private const string EnableAgentCommand = "/enable-agent";
    private const string DeleteAgentCommand = "/delete-agent";

    public static bool TryResolve(
        ChannelInboundEvent evt,
        out AgentBuilderFlowDecision? decision)
    {
        var resolution = Resolve(evt);
        decision = resolution.Decision;
        return resolution.IsMatchedKnownAgentBuilderCommand;
    }

    public static NyxRelayAgentBuilderFlowResolution Resolve(ChannelInboundEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (string.IsNullOrWhiteSpace(evt.Text))
            return NyxRelayAgentBuilderFlowResolution.NonSlashText();

        var trimmedText = evt.Text.TrimStart();
        if (!trimmedText.StartsWith('/'))
            return NyxRelayAgentBuilderFlowResolution.NonSlashText();

        var tokens = ChannelTextCommandParser.Tokenize(trimmedText);
        if (tokens.Count == 0)
            return NyxRelayAgentBuilderFlowResolution.NonSlashText();

        var command = tokens[0];
        if (!IsKnownCommand(command))
            return NyxRelayAgentBuilderFlowResolution.UnknownSlashCommand();

        if (!IsPrivateChat(evt.ChatType))
        {
            return NyxRelayAgentBuilderFlowResolution.PrivateChatRejected(
                AgentBuilderFlowDecision.DirectReply(BuildPrivateChatRestrictionReply(command)));
        }

        return TryResolveKnownCommand(command, tokens, out var decision)
            ? NyxRelayAgentBuilderFlowResolution.KnownAgentBuilderCommand(decision!)
            : NyxRelayAgentBuilderFlowResolution.UnknownSlashCommand();
    }

    public static MessageContent FormatToolResult(AgentBuilderFlowDecision decision, string toolResultJson)
    {
        ArgumentNullException.ThrowIfNull(decision);

        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            return decision.ToolAction switch
            {
                "list_agents" => AgentBuilderCardContent.FormatListAgentsResult(doc.RootElement),
                "agent_status" => FormatAgentStatusCard(doc.RootElement),
                "run_agent" => TextContent(FormatRunAgentResult(doc.RootElement)),
                "disable_agent" => TextContent(FormatLifecycleStatusResult("Agent disabled.", doc.RootElement)),
                "enable_agent" => TextContent(FormatLifecycleStatusResult("Agent enabled.", doc.RootElement)),
                "delete_agent" => TextContent(FormatDeleteAgentResult(doc.RootElement)),
                _ => TextContent(toolResultJson),
            };
        }
        catch (JsonException)
        {
            return TextContent(toolResultJson);
        }
    }

    private static MessageContent TextContent(string text) => AgentBuilderJson.TextContent(text);

    private static bool IsKnownCommand(string command) =>
        command is ListAgentsCommand
            or AgentStatusCommand
            or RunAgentCommand
            or DisableAgentCommand
            or EnableAgentCommand
            or DeleteAgentCommand;

    private static bool IsPrivateChat(string? chatType) =>
        string.Equals(chatType, PrivateChatType, StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveKnownCommand(
        string command,
        IReadOnlyList<string> tokens,
        out AgentBuilderFlowDecision? decision)
    {
        switch (command)
        {
            case ListAgentsCommand:
                decision = AgentBuilderFlowDecision.ToolCall("list_agents", """{"action":"list_agents"}""");
                return true;

            case AgentStatusCommand:
                return TryResolveSimpleAgentAction(tokens, "agent_status", "Usage: /agent-status <agent_id>", out decision);

            case RunAgentCommand:
                return TryResolveSimpleAgentAction(tokens, "run_agent", "Usage: /run-agent <agent_id>", out decision);

            case DisableAgentCommand:
                return TryResolveSimpleAgentAction(tokens, "disable_agent", "Usage: /disable-agent <agent_id>", out decision);

            case EnableAgentCommand:
                return TryResolveSimpleAgentAction(tokens, "enable_agent", "Usage: /enable-agent <agent_id>", out decision);

            case DeleteAgentCommand:
                return TryResolveDeleteAgent(tokens, out decision);

            default:
                decision = null;
                return false;
        }
    }

    private static bool TryResolveSimpleAgentAction(
        IReadOnlyList<string> tokens,
        string action,
        string usage,
        out AgentBuilderFlowDecision? decision)
    {
        decision = null;
        if (tokens.Count < 2 || string.IsNullOrWhiteSpace(tokens[1]))
        {
            decision = AgentBuilderFlowDecision.DirectReply(usage);
            return true;
        }

        decision = AgentBuilderFlowDecision.ToolCall(
            action,
            JsonSerializer.Serialize(new
            {
                action,
                agent_id = tokens[1].Trim(),
            }));
        return true;
    }

    private static bool TryResolveDeleteAgent(
        IReadOnlyList<string> tokens,
        out AgentBuilderFlowDecision? decision)
    {
        decision = null;
        if (tokens.Count < 2 || string.IsNullOrWhiteSpace(tokens[1]))
        {
            decision = AgentBuilderFlowDecision.DirectReply("Usage: /delete-agent <agent_id> confirm");
            return true;
        }

        var agentId = tokens[1].Trim();
        var confirmed = tokens.Count > 2 &&
                        string.Equals(tokens[2], "confirm", StringComparison.OrdinalIgnoreCase);
        if (!confirmed)
        {
            decision = AgentBuilderFlowDecision.DirectReply(
                $"Delete confirmation required.\nRun `/delete-agent {agentId} confirm` to continue.");
            return true;
        }

        decision = AgentBuilderFlowDecision.ToolCall(
            "delete_agent",
            JsonSerializer.Serialize(new
            {
                action = "delete_agent",
                agent_id = agentId,
                confirm = true,
            }));
        return true;
    }

    /// <summary>
    /// Renders <c>/agent-status &lt;agent_id&gt;</c> as an interactive card with action buttons
    /// (Run, Disable, Enable, Delete). Each button submits the corresponding
    /// <c>agent_builder_action</c> with the agent_id as an argument so
    /// <see cref="AgentBuilderCardFlow"/> can route the click to the existing tool action without
    /// the user having to retype the id.
    /// </summary>
    private static MessageContent FormatAgentStatusCard(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return TextContent($"Agent status failed: {error}");

        var agentId = ReadString(root, "agent_id") ?? "unknown-agent";
        var template = ReadString(root, "template") ?? "unknown-template";
        var status = ReadString(root, "status") ?? "unknown";
        var schedule = $"{ReadString(root, "schedule_cron") ?? "n/a"} ({ReadString(root, "schedule_timezone") ?? "n/a"})";
        var lastRun = ReadString(root, "last_run_at") ?? "n/a";
        var nextRun = ReadString(root, "next_scheduled_run") ?? "n/a";
        var lastError = NormalizeOptional(ReadString(root, "last_error"));
        var note = NormalizeOptional(ReadString(root, "note"));

        var bodyLines = new List<string>
        {
            $"Agent ID: `{agentId}`",
            $"Template: `{template}`",
            $"Status: `{status}`",
            $"Schedule: `{schedule}`",
            $"Last run: `{lastRun}`",
            $"Next run: `{nextRun}`",
        };
        if (lastError is not null)
            bodyLines.Add($"Last error: {lastError}");
        if (note is not null)
            bodyLines.Add(note);

        var content = new MessageContent();
        content.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = $"agent_status:{agentId}",
            Title = "Agent Status",
            Text = string.Join("\n", bodyLines),
        });

        var isRunning = string.Equals(status, SkillRunnerDefaults.StatusRunning, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(status, SkillRunnerDefaults.StatusError, StringComparison.OrdinalIgnoreCase);
        content.Actions.Add(BuildAgentScopedButton("Run Now", "run_agent", agentId, isPrimary: isRunning));
        content.Actions.Add(BuildAgentScopedButton("Disable", "disable_agent", agentId, isPrimary: false));
        content.Actions.Add(BuildAgentScopedButton("Enable", "enable_agent", agentId, isPrimary: false));
        var deleteButton = BuildAgentScopedButton("Delete", "delete_agent", agentId, isPrimary: false);
        deleteButton.IsDanger = true;
        deleteButton.Arguments["confirm"] = "true";
        content.Actions.Add(deleteButton);
        content.Actions.Add(BuildButton("Back to Agents", "list_agents", isPrimary: false));

        return content;
    }

    private static ActionElement BuildButton(string label, string agentBuilderAction, bool isPrimary)
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

    private static ActionElement BuildAgentScopedButton(string label, string agentBuilderAction, string agentId, bool isPrimary)
    {
        var button = BuildButton(label, agentBuilderAction, isPrimary);
        button.Arguments["agent_id"] = agentId;
        return button;
    }

    private static string FormatRunAgentResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"Run agent failed: {error}";

        var agentId = ReadString(root, "agent_id") ?? "unknown-agent";
        return BuildTextBlock(
            "Manual run accepted.",
            $"Agent ID: {agentId}",
            NormalizeOptional(ReadString(root, "note")),
            $"Check progress with /agent-status {agentId}");
    }

    private static string FormatLifecycleStatusResult(string headline, JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"{headline} Failed: {error}";

        return BuildTextBlock(
            headline,
            $"Agent ID: {ReadString(root, "agent_id") ?? "unknown-agent"}",
            $"Status: {ReadString(root, "status") ?? "unknown"}",
            NormalizeOptional(ReadString(root, "note")));
    }

    private static string FormatDeleteAgentResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"Delete agent failed: {error}";

        if (string.Equals(ReadString(root, "status"), "confirm_required", StringComparison.OrdinalIgnoreCase))
        {
            var agentId = ReadString(root, "agent_id") ?? "<agent_id>";
            return $"Delete confirmation required.\nRun `/delete-agent {agentId} confirm` to continue.";
        }

        return BuildTextBlock(
            "Delete accepted.",
            $"Agent ID: {ReadString(root, "agent_id") ?? "unknown-agent"}",
            $"Revoked API key: {ReadString(root, "revoked_api_key_id") ?? "n/a"}",
            NormalizeOptional(ReadString(root, "delete_notice")),
            NormalizeOptional(ReadString(root, "note")),
            "Run /agents to refresh the registry view.");
    }

    private static bool TryReadError(JsonElement root, out string error) =>
        AgentBuilderJson.TryReadError(root, out error);

    private static string? ReadString(JsonElement element, string propertyName) =>
        AgentBuilderJson.TryReadString(element, propertyName);

    private static string BuildPrivateChatRestrictionReply(string command) =>
        $"`{command}` only works in a private chat with this bot. Please DM me and run `{command}` again.";

    private static string BuildTextBlock(params string?[] lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            var normalized = NormalizeOptional(line);
            if (normalized is null)
                continue;

            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append(normalized);
        }

        return builder.ToString();
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}

public enum NyxRelayAgentBuilderFlowOutcome
{
    KnownAgentBuilderCommand,
    NonSlashText,
    UnknownSlashCommandPassToLlm,
    PrivateChatRejected,
}

public sealed record NyxRelayAgentBuilderFlowResolution(
    NyxRelayAgentBuilderFlowOutcome Outcome,
    AgentBuilderFlowDecision? Decision)
{
    public bool IsMatchedKnownAgentBuilderCommand =>
        Outcome is NyxRelayAgentBuilderFlowOutcome.KnownAgentBuilderCommand
            or NyxRelayAgentBuilderFlowOutcome.PrivateChatRejected;

    public bool ShouldPassToLlm =>
        Outcome is NyxRelayAgentBuilderFlowOutcome.NonSlashText
            or NyxRelayAgentBuilderFlowOutcome.UnknownSlashCommandPassToLlm;

    public static NyxRelayAgentBuilderFlowResolution KnownAgentBuilderCommand(
        AgentBuilderFlowDecision decision) =>
        new(NyxRelayAgentBuilderFlowOutcome.KnownAgentBuilderCommand, decision);

    public static NyxRelayAgentBuilderFlowResolution NonSlashText() =>
        new(NyxRelayAgentBuilderFlowOutcome.NonSlashText, null);

    public static NyxRelayAgentBuilderFlowResolution UnknownSlashCommand() =>
        new(NyxRelayAgentBuilderFlowOutcome.UnknownSlashCommandPassToLlm, null);

    public static NyxRelayAgentBuilderFlowResolution PrivateChatRejected(
        AgentBuilderFlowDecision decision) =>
        new(NyxRelayAgentBuilderFlowOutcome.PrivateChatRejected, decision);
}
