using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aevatar.GAgents.ChannelRuntime;

internal static class NyxRelayAgentBuilderFlow
{
    private const string PrivateChatType = "p2p";
    private const string DailyCommand = "/daily";
    private const string SocialMediaCommand = "/social-media";
    private const string SocialMediaAlias = "/create-social-media";
    private const string ListTemplatesCommand = "/templates";
    private const string ListAgentsCommand = "/agents";
    private const string AgentStatusCommand = "/agent-status";
    private const string RunAgentCommand = "/run-agent";
    private const string DisableAgentCommand = "/disable-agent";
    private const string EnableAgentCommand = "/enable-agent";
    private const string DeleteAgentCommand = "/delete-agent";
    private const string DefaultScheduleTime = "09:00";

    public static bool TryResolve(ChannelInboundEvent evt, out AgentBuilderFlowDecision? decision)
    {
        ArgumentNullException.ThrowIfNull(evt);
        decision = null;

        if (string.IsNullOrWhiteSpace(evt.Text))
            return false;

        var trimmedText = evt.Text.TrimStart();
        if (!trimmedText.StartsWith('/'))
            return false;

        var tokens = ChannelTextCommandParser.Tokenize(trimmedText);
        if (tokens.Count == 0)
            return false;

        var command = tokens[0];
        if (!IsKnownCommand(command))
        {
            decision = AgentBuilderFlowDecision.DirectReply(BuildUnknownCommandReply(command));
            return true;
        }

        if (!IsPrivateChat(evt.ChatType))
        {
            decision = AgentBuilderFlowDecision.DirectReply(BuildPrivateChatRestrictionReply(command));
            return true;
        }

        return TryResolveKnownCommand(command, tokens, evt.ConversationId, out decision);
    }

    public static string FormatToolResult(AgentBuilderFlowDecision decision, string toolResultJson)
    {
        ArgumentNullException.ThrowIfNull(decision);

        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            return decision.ToolAction switch
            {
                "create_daily_report" => FormatCreateDailyReportResult(doc.RootElement),
                "create_social_media" => FormatCreateSocialMediaResult(doc.RootElement),
                "list_templates" => FormatListTemplatesResult(doc.RootElement),
                "list_agents" => FormatListAgentsResult(doc.RootElement),
                "agent_status" => FormatAgentStatusResult(doc.RootElement),
                "run_agent" => FormatRunAgentResult(doc.RootElement),
                "disable_agent" => FormatLifecycleStatusResult("Agent disabled.", doc.RootElement),
                "enable_agent" => FormatLifecycleStatusResult("Agent enabled.", doc.RootElement),
                "delete_agent" => FormatDeleteAgentResult(doc.RootElement),
                _ => toolResultJson,
            };
        }
        catch (JsonException)
        {
            return toolResultJson;
        }
    }

    private static bool IsKnownCommand(string command) =>
        command is DailyCommand
            or SocialMediaCommand or SocialMediaAlias
            or ListTemplatesCommand
            or ListAgentsCommand
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
        string? conversationId,
        out AgentBuilderFlowDecision? decision)
    {
        switch (command)
        {
            case DailyCommand:
                return TryResolveDailyReport(tokens, conversationId, out decision);

            case SocialMediaCommand:
            case SocialMediaAlias:
                return TryResolveSocialMedia(tokens, conversationId, out decision);

            case ListTemplatesCommand:
                decision = AgentBuilderFlowDecision.ToolCall("list_templates", """{"action":"list_templates"}""");
                return true;

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

    private static bool TryResolveDailyReport(
        IReadOnlyList<string> tokens,
        string? conversationId,
        out AgentBuilderFlowDecision? decision)
    {
        decision = null;
        if (tokens.Count == 1)
        {
            decision = AgentBuilderFlowDecision.DirectReply(BuildDailyReportHelpText());
            return true;
        }

        var args = ChannelTextCommandParser.ParseNamedArguments(tokens);
        var githubUsername = GetOptional(args, "github_username")
                             ?? FirstPositionalArgument(tokens);
        if (string.IsNullOrWhiteSpace(githubUsername))
        {
            decision = AgentBuilderFlowDecision.DirectReply(
                "github_username is required.\n\n" + BuildDailyReportHelpText());
            return true;
        }

        if (!TryResolveSchedule(args, out var scheduleCron, out var scheduleTimezone, out var error))
        {
            decision = AgentBuilderFlowDecision.DirectReply(error! + "\n\n" + BuildDailyReportHelpText());
            return true;
        }

        var repositories = GetOptional(args, "repositories");
        var runImmediately = ResolveRunImmediately(args);
        decision = AgentBuilderFlowDecision.ToolCall(
            "create_daily_report",
            JsonSerializer.Serialize(new
            {
                action = "create_agent",
                template = "daily_report",
                github_username = githubUsername,
                repositories,
                schedule_cron = scheduleCron,
                schedule_timezone = scheduleTimezone,
                run_immediately = runImmediately,
                conversation_id = NormalizeOptional(conversationId),
            }));
        return true;
    }

    private static bool TryResolveSocialMedia(
        IReadOnlyList<string> tokens,
        string? conversationId,
        out AgentBuilderFlowDecision? decision)
    {
        decision = null;
        if (tokens.Count == 1)
        {
            decision = AgentBuilderFlowDecision.DirectReply(BuildSocialMediaHelpText());
            return true;
        }

        var args = ChannelTextCommandParser.ParseNamedArguments(tokens);
        var topic = GetOptional(args, "topic") ?? FirstPositionalArgument(tokens);
        if (string.IsNullOrWhiteSpace(topic))
        {
            decision = AgentBuilderFlowDecision.DirectReply(
                "topic is required.\n\n" + BuildSocialMediaHelpText());
            return true;
        }

        if (!TryResolveSchedule(args, out var scheduleCron, out var scheduleTimezone, out var error))
        {
            decision = AgentBuilderFlowDecision.DirectReply(error! + "\n\n" + BuildSocialMediaHelpText());
            return true;
        }

        decision = AgentBuilderFlowDecision.ToolCall(
            "create_social_media",
            JsonSerializer.Serialize(new
            {
                action = "create_agent",
                template = "social_media",
                topic,
                audience = GetOptional(args, "audience"),
                style = GetOptional(args, "style"),
                schedule_cron = scheduleCron,
                schedule_timezone = scheduleTimezone,
                run_immediately = ResolveRunImmediately(args),
                conversation_id = NormalizeOptional(conversationId),
            }));
        return true;
    }

    private static string? FirstPositionalArgument(IReadOnlyList<string> tokens)
    {
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (string.IsNullOrWhiteSpace(token))
                continue;
            if (token.IndexOf('=', StringComparison.Ordinal) >= 0)
                continue;
            return token.Trim();
        }
        return null;
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

    private static string FormatCreateDailyReportResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"Create daily report agent failed: {error}";

        var status = ReadString(root, "status") ?? "accepted";
        if (string.Equals(status, "credentials_required", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "oauth_required", StringComparison.OrdinalIgnoreCase))
        {
            var providerId = ReadString(root, "provider_id") ?? "unknown-provider";
            var url = ReadString(root, "authorization_url")
                      ?? ReadString(root, "auth_url")
                      ?? ReadString(root, "url")
                      ?? ReadString(root, "documentation_url");
            var note = ReadString(root, "note") ?? "Finish the GitHub authorization step, then run /daily again.";

            return BuildTextBlock(
                string.Equals(status, "oauth_required", StringComparison.OrdinalIgnoreCase)
                    ? "GitHub authorization required."
                    : "GitHub credentials required.",
                note,
                $"Provider ID: {providerId}",
                string.IsNullOrWhiteSpace(url) ? null : $"Open: {url}",
                BuildDailyReportHelpText());
        }

        return BuildTextBlock(
            "Daily report agent registered.",
            $"Agent ID: {ReadString(root, "agent_id") ?? "unknown-agent"}",
            $"Next scheduled run: {ReadString(root, "next_scheduled_run") ?? "pending"}",
            NormalizeOptional(ReadString(root, "note")),
            "Next commands: /agents, /agent-status <agent_id>, /run-agent <agent_id>");
    }

    private static string FormatCreateSocialMediaResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"Create social media agent failed: {error}";

        return BuildTextBlock(
            "Social media agent registered.",
            $"Agent ID: {ReadString(root, "agent_id") ?? "unknown-agent"}",
            $"Workflow ID: {ReadString(root, "workflow_id") ?? "pending"}",
            $"Next scheduled run: {ReadString(root, "next_scheduled_run") ?? "pending"}",
            NormalizeOptional(ReadString(root, "note")),
            "Approvals will arrive as text instructions in this chat. Use /approve or /reject exactly as shown.",
            "Next commands: /agents, /agent-status <agent_id>, /run-agent <agent_id>");
    }

    private static string FormatListTemplatesResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"List templates failed: {error}";

        if (!root.TryGetProperty("templates", out var templatesElement) ||
            templatesElement.ValueKind != JsonValueKind.Array ||
            templatesElement.GetArrayLength() == 0)
        {
            return "No templates available.";
        }

        var lines = new List<string> { "Available templates:" };
        foreach (var item in templatesElement.EnumerateArray())
        {
            var name = ReadString(item, "name") ?? "unknown-template";
            var description = ReadString(item, "description") ?? "No description.";
            lines.Add($"- {name}: {description}");
        }

        lines.Add(string.Empty);
        lines.Add("Examples:");
        lines.Add(BuildDailyReportCommandExample());
        lines.Add(BuildSocialMediaCommandExample());
        return string.Join('\n', lines);
    }

    private static string FormatListAgentsResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"List agents failed: {error}";

        if (!root.TryGetProperty("agents", out var agentsElement) ||
            agentsElement.ValueKind != JsonValueKind.Array ||
            agentsElement.GetArrayLength() == 0)
        {
            return BuildTextBlock(
                "No agents found.",
                "Create one with:",
                BuildDailyReportCommandExample(),
                BuildSocialMediaCommandExample());
        }

        var lines = new List<string> { "Current agents:" };
        foreach (var item in agentsElement.EnumerateArray())
        {
            var agentId = ReadString(item, "agent_id") ?? "unknown-agent";
            var template = ReadString(item, "template") ?? "unknown-template";
            var status = ReadString(item, "status") ?? "unknown";
            var nextRun = ReadString(item, "next_scheduled_run") ?? "pending";
            lines.Add($"- {agentId}: template={template}, status={status}, next_run={nextRun}");
        }

        lines.Add(string.Empty);
        lines.Add("Next commands: /agent-status <agent_id>, /run-agent <agent_id>, /disable-agent <agent_id>, /enable-agent <agent_id>, /delete-agent <agent_id> confirm");
        return string.Join('\n', lines);
    }

    private static string FormatAgentStatusResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"Agent status failed: {error}";

        var agentId = ReadString(root, "agent_id") ?? "unknown-agent";
        return BuildTextBlock(
            "Agent status:",
            $"Agent ID: {agentId}",
            $"Template: {ReadString(root, "template") ?? "unknown-template"}",
            $"Status: {ReadString(root, "status") ?? "unknown"}",
            $"Schedule: {ReadString(root, "schedule_cron") ?? "n/a"} ({ReadString(root, "schedule_timezone") ?? "n/a"})",
            $"Last run: {ReadString(root, "last_run_at") ?? "n/a"}",
            $"Next run: {ReadString(root, "next_scheduled_run") ?? "n/a"}",
            NormalizeOptional(ReadString(root, "last_error")) is { } lastError ? $"Last error: {lastError}" : null,
            NormalizeOptional(ReadString(root, "note")),
            $"Next commands: /run-agent {agentId}, /disable-agent {agentId}, /enable-agent {agentId}, /delete-agent {agentId} confirm");
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

    private static bool TryResolveSchedule(
        IReadOnlyDictionary<string, string> args,
        out string? scheduleCron,
        out string scheduleTimezone,
        out string? error)
    {
        scheduleCron = null;
        error = null;

        scheduleTimezone = GetOptional(args, "schedule_timezone") ?? SkillRunnerDefaults.DefaultTimezone;
        var rawCron = GetOptional(args, "schedule_cron");
        if (!string.IsNullOrWhiteSpace(rawCron))
        {
            scheduleCron = rawCron;
            return true;
        }

        var rawTime = GetOptional(args, "schedule_time");
        var normalized = rawTime ?? DefaultScheduleTime;
        if (!TimeOnly.TryParseExact(
                normalized,
                ["HH:mm", "H:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            error = "schedule_time must use HH:mm, for example 09:00.";
            return false;
        }

        scheduleCron = $"{time.Minute} {time.Hour} * * *";
        return true;
    }

    private static bool ResolveRunImmediately(IReadOnlyDictionary<string, string> args)
    {
        var raw = GetOptional(args, "run_immediately");
        return !bool.TryParse(raw, out var parsed) || parsed;
    }

    private static string? GetOptional(IReadOnlyDictionary<string, string> args, string key)
    {
        if (!args.TryGetValue(key, out var raw))
            return null;

        return NormalizeOptional(raw);
    }

    private static bool TryReadError(JsonElement root, out string error)
    {
        error = ReadString(root, "error") ?? string.Empty;
        return error.Length > 0;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null,
        };
    }

    private static string BuildDailyReportHelpText() =>
        BuildTextBlock(
            "Daily report agent command",
            "Required: github_username plus either schedule_time or schedule_cron.",
            $"Example: {BuildDailyReportCommandExample()}",
            "Optional: repositories=owner/repo,owner/repo schedule_timezone=Asia/Singapore run_immediately=false");

    private static string BuildSocialMediaHelpText() =>
        BuildTextBlock(
            "Social media agent command",
            "Required: topic plus either schedule_time or schedule_cron.",
            $"Example: {BuildSocialMediaCommandExample()}",
            "Optional: audience=\"Developers\" style=\"Confident and concise\" schedule_timezone=Asia/Singapore run_immediately=false");

    private static string BuildDailyReportCommandExample() =>
        "/daily github_username=alice schedule_time=09:00 repositories=owner/repo";

    private static string BuildSocialMediaCommandExample() =>
        "/social-media topic=\"Launch update\" schedule_time=10:30 audience=\"Developers\" style=\"Confident and concise\"";

    private static string BuildUnknownCommandReply(string command) =>
        BuildTextBlock(
            $"Unknown command: {command}",
            "Supported commands:",
            BuildDailyReportCommandExample(),
            BuildSocialMediaCommandExample(),
            "/templates",
            "/agents",
            "/agent-status <agent_id>",
            "/run-agent <agent_id>",
            "/disable-agent <agent_id>",
            "/enable-agent <agent_id>",
            "/delete-agent <agent_id> confirm");

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
