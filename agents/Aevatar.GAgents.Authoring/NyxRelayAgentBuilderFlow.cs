using System.Globalization;
using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.Authoring;

public static class NyxRelayAgentBuilderFlow
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

    public static MessageContent FormatToolResult(AgentBuilderFlowDecision decision, string toolResultJson)
    {
        ArgumentNullException.ThrowIfNull(decision);

        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            return decision.ToolAction switch
            {
                "create_daily_report" => FormatCreateDailyReportResult(doc.RootElement),
                "create_social_media" => TextContent(FormatCreateSocialMediaResult(doc.RootElement)),
                "list_templates" => TextContent(FormatListTemplatesResult(doc.RootElement)),
                "list_agents" => FormatListAgentsCard(doc.RootElement),
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

    private static MessageContent TextContent(string text) => new() { Text = text };

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
        var args = ChannelTextCommandParser.ParseNamedArguments(tokens);
        var githubUsername = NormalizeOptional(
            GetOptional(args, "github_username") ?? FirstPositionalArgument(tokens));

        if (!TryResolveSchedule(args, out var scheduleCron, out var scheduleTimezone, out var error))
        {
            decision = AgentBuilderFlowDecision.DirectReply(error! + "\n\n" + BuildDailyReportHelpText());
            return true;
        }

        var repositories = GetOptional(args, "repositories");
        var runImmediately = ResolveRunImmediately(args);
        // When the user typed a positional username we persist it as their default so the next /daily
        // call auto-resolves via the saved preference fallback inside AgentBuilderTool.
        var savePreference = githubUsername is not null;
        decision = AgentBuilderFlowDecision.ToolCall(
            "create_daily_report",
            JsonSerializer.Serialize(new
            {
                action = "create_agent",
                template = "daily_report",
                github_username = githubUsername,
                save_github_username_preference = savePreference,
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

    private static MessageContent FormatCreateDailyReportResult(JsonElement root) =>
        AgentBuilderCardContent.FormatDailyReportToolReply(root);

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
            "Approvals will arrive as interactive cards in this chat. Text commands such as /approve and /reject still work as fallback.",
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

    /// <summary>
    /// Renders <c>/agents</c> as an interactive Lark card. Each agent gets a section block with
    /// status fields and a "Status" button that triggers <c>agent_builder_action=agent_status</c>
    /// (handled by <see cref="AgentBuilderCardFlow"/>); a footer button cluster offers shortcuts
    /// to create another agent or browse templates. Empty result keeps the existing helper-text
    /// reply since there are no per-agent buttons to render.
    /// </summary>
    private static MessageContent FormatListAgentsCard(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return TextContent($"List agents failed: {error}");

        var content = new MessageContent();

        if (!root.TryGetProperty("agents", out var agentsElement) ||
            agentsElement.ValueKind != JsonValueKind.Array ||
            agentsElement.GetArrayLength() == 0)
        {
            content.Cards.Add(new CardBlock
            {
                Kind = CardBlockKind.Section,
                BlockId = "agents_empty",
                Title = "No agents yet",
                Text = "Create one with `/daily` for a daily GitHub report or `/social-media` for a social-media drafter.",
            });
            content.Actions.Add(BuildButton("Create Daily Report", "open_daily_report_form", isPrimary: true));
            content.Actions.Add(BuildButton("Create Social Media", "open_social_media_form", isPrimary: false));
            return content;
        }

        var summary = new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = "agents_summary",
            Title = "Your agents",
            Text = "Tap **Status** under any agent to drill in. Action buttons there run, disable/enable, or delete the agent.",
        };
        content.Cards.Add(summary);

        foreach (var item in agentsElement.EnumerateArray())
        {
            var agentId = ReadString(item, "agent_id") ?? "unknown-agent";
            var template = ReadString(item, "template") ?? "unknown-template";
            var status = ReadString(item, "status") ?? "unknown";
            var nextRun = ReadString(item, "next_scheduled_run") ?? "pending";
            var lastRun = NormalizeOptional(ReadString(item, "last_run_at"));

            var card = new CardBlock
            {
                Kind = CardBlockKind.Section,
                BlockId = $"agent_row:{agentId}",
                Title = $"`{agentId}`",
                Text = $"Template: `{template}` · Status: `{status}`\nNext run: `{nextRun}`{(lastRun is null ? string.Empty : $" · Last run: `{lastRun}`")}",
            };
            content.Cards.Add(card);

            // Per-agent "Status" button: triggers `agent_status` action which AgentBuilderCardFlow
            // already handles and re-renders as a status card with the run / lifecycle actions.
            content.Actions.Add(BuildAgentScopedButton(
                label: $"Status: {ShortenAgentId(agentId)}",
                agentBuilderAction: "agent_status",
                agentId: agentId,
                isPrimary: false));
        }

        // Footer shortcut row mirrors what AgentBuilderCardFlow renders on the dedicated card
        // path so users have one consistent UX whether they typed `/agents` or arrived via card.
        content.Actions.Add(BuildButton("Create Daily Report", "open_daily_report_form", isPrimary: false));
        content.Actions.Add(BuildButton("Create Social Media", "open_social_media_form", isPrimary: false));
        content.Actions.Add(BuildButton("Templates", "list_templates", isPrimary: false));

        return content;
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

    /// <summary>
    /// Renders <c>/agent-status &lt;agent_id&gt;</c> as an interactive card with action buttons
    /// (Run, Disable, Enable, Delete). Each button submits the corresponding
    /// <c>agent_builder_action</c> with the agent_id as an argument so
    /// <see cref="AgentBuilderCardFlow"/> can route the click to the existing tool action without
    /// the user having to retype the id. Mirrors the card produced by the card-flow path so the
    /// text-command and card-flow surfaces stay visually consistent.
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

        // Lifecycle buttons mirror the legacy text "Next commands: ..." line. Disable and Enable
        // are both shown so the user can flip status either direction without typing; the click
        // handler enforces the invariants. Delete is marked danger so Lark renders it red and the
        // user has a final visual confirm before submitting.
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

    /// <summary>
    /// Compresses long agent ids (e.g. <c>skill-runner-94d754dfdfbb416aa5a676cecd0d7a71</c>) into
    /// a 10-char suffix so per-agent button labels stay readable in narrow Lark cards. The full
    /// id is still carried in the button's <c>arguments</c> so the click handler routes correctly.
    /// </summary>
    private static string ShortenAgentId(string agentId)
    {
        if (string.IsNullOrEmpty(agentId) || agentId.Length <= 14)
            return agentId;

        return $"…{agentId[^10..]}";
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
            "GitHub username can be passed explicitly, or omitted to reuse a saved preference when available.",
            "Schedule defaults to 09:00 if schedule_time and schedule_cron are both omitted.",
            $"Example: {BuildDailyReportCommandExample()}",
            "Optional: github_username (otherwise uses your saved preference or connected GitHub login), repositories=owner/repo,owner/repo schedule_timezone=Asia/Singapore run_immediately=false");

    private static string BuildSocialMediaHelpText() =>
        BuildTextBlock(
            "Social media agent command",
            "Required: topic plus either schedule_time or schedule_cron.",
            $"Example: {BuildSocialMediaCommandExample()}",
            "Optional: audience=\"Developers\" style=\"Confident and concise\" schedule_timezone=Asia/Singapore run_immediately=false");

    private static string BuildDailyReportCommandExample() =>
        "/daily [github_username] schedule_time=09:00 repositories=owner/repo";

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
