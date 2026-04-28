using System.Globalization;
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
    private const string OpenDailyReportFormAction = AgentBuilderActionIds.OpenDailyReportForm;
    private const string OpenSocialMediaFormAction = AgentBuilderActionIds.OpenSocialMediaForm;
    private const string DailyReportAction = AgentBuilderActionIds.DailyReport;
    private const string SocialMediaAction = AgentBuilderActionIds.SocialMedia;
    private const string ListTemplatesAction = AgentBuilderActionIds.ListTemplates;
    private const string ListAgentsAction = AgentBuilderActionIds.ListAgents;
    private const string AgentStatusAction = AgentBuilderActionIds.AgentStatus;
    private const string RunAgentAction = AgentBuilderActionIds.RunAgent;
    private const string DisableAgentAction = AgentBuilderActionIds.DisableAgent;
    private const string EnableAgentAction = AgentBuilderActionIds.EnableAgent;
    private const string ConfirmDeleteAgentAction = AgentBuilderActionIds.ConfirmDeleteAgent;
    private const string DeleteAgentAction = AgentBuilderActionIds.DeleteAgent;
    private const string DefaultScheduleTime = "09:00";
    private const string SocialMediaCommand = "/social-media";
    private const string AgentStatusCommand = "/agent-status";
    private const string RunAgentCommand = "/run-agent";
    private const string DisableAgentCommand = "/disable-agent";
    private const string EnableAgentCommand = "/enable-agent";
    private const string DeleteAgentCommand = "/delete-agent";

    private static readonly HashSet<string> LaunchIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        "/daily",
        "create daily report",
        "创建日报助手",
        "创建日报agent",
    };

    private static readonly HashSet<string> SocialMediaIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        SocialMediaCommand,
        "/create-social-media",
        "create social media",
        "创建社媒助手",
        "创建社媒agent",
    };

    private static readonly HashSet<string> ListIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        "/agents",
        "list agents",
        "我的助手",
    };

    private static readonly HashSet<string> TemplateIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        "/templates",
        "/agent-templates",
        "list templates",
        "模板列表",
    };

    public static bool TryResolve(ChannelInboundEvent evt, out AgentBuilderFlowDecision? decision) =>
        TryResolve(evt, preferredGithubUsername: null, out decision);

    public static async Task<AgentBuilderFlowDecision?> TryResolveAsync(
        ChannelInboundEvent evt,
        IUserConfigQueryPort? userConfigQueryPort,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        string? preferredGithubUsername = null;
        if (ShouldLoadPreferredGithubUsername(evt) && userConfigQueryPort is not null)
        {
            try
            {
                preferredGithubUsername = (await userConfigQueryPort.GetAsync(
                    ChannelUserConfigScope.FromInboundEvent(evt),
                    ct)).GithubUsername;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                preferredGithubUsername = null;
            }
        }

        TryResolve(evt, preferredGithubUsername, out var decision);
        return decision;
    }

    private static bool TryResolve(
        ChannelInboundEvent evt,
        string? preferredGithubUsername,
        out AgentBuilderFlowDecision? decision)
    {
        ArgumentNullException.ThrowIfNull(evt);
        decision = null;

        if (IsPrivateChatText(evt))
        {
            var normalized = NormalizeText(evt.Text);
            if (LaunchIntents.Contains(normalized))
            {
                // Direct webhook deployments hit this path (no Nyx relay in front); the pre-serialized
                // Lark JSON card from BuildDailyReportCard used to land in MessageContent.Text and
                // render as raw JSON. Route through the channel-neutral form builder so the composer
                // emits a real interactive card.
                decision = AgentBuilderFlowDecision.DirectReply(
                    AgentBuilderCardContent.BuildDailyReportForm(preferredGithubUsername));
                return true;
            }

            if (SocialMediaIntents.Contains(normalized))
            {
                decision = AgentBuilderFlowDecision.DirectReply(AgentBuilderCardContent.BuildSocialMediaForm());
                return true;
            }

            if (ListIntents.Contains(normalized))
            {
                decision = AgentBuilderFlowDecision.ToolCall(ListAgentsAction, """{"action":"list_agents"}""");
                return true;
            }

            if (TemplateIntents.Contains(normalized))
            {
                decision = AgentBuilderFlowDecision.ToolCall(ListTemplatesAction, """{"action":"list_templates"}""");
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

        switch ((action ?? string.Empty).Trim())
        {
            case OpenDailyReportFormAction:
                decision = AgentBuilderFlowDecision.DirectReply(
                    AgentBuilderCardContent.BuildDailyReportForm(preferredGithubUsername));
                return true;

            case OpenSocialMediaFormAction:
                decision = AgentBuilderFlowDecision.DirectReply(AgentBuilderCardContent.BuildSocialMediaForm());
                return true;

            case DailyReportAction:
                if (!TryBuildCreateDailyReportArguments(evt, out var argumentsJson, out var validationError))
                {
                    decision = AgentBuilderFlowDecision.DirectReply(validationError!);
                    return true;
                }

                decision = AgentBuilderFlowDecision.ToolCall(DailyReportAction, argumentsJson!);
                return true;

            case SocialMediaAction:
                if (!TryBuildCreateSocialMediaArguments(evt, out argumentsJson, out validationError))
                {
                    decision = AgentBuilderFlowDecision.DirectReply(validationError!);
                    return true;
                }

                decision = AgentBuilderFlowDecision.ToolCall(SocialMediaAction, argumentsJson!);
                return true;

            case ListAgentsAction:
                decision = AgentBuilderFlowDecision.ToolCall(ListAgentsAction, """{"action":"list_agents"}""");
                return true;

            case ListTemplatesAction:
                // The /agents card surfaces a `Templates` button (also reachable via the
                // text-flow `/templates` slash command). Without this branch, clicking the
                // button leaves the user with an unhandled card action and no feedback.
                decision = AgentBuilderFlowDecision.ToolCall(ListTemplatesAction, """{"action":"list_templates"}""");
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

                // Use the MessageContent overload so the relay composer renders this as a real
                // Lark card instead of forwarding a JSON-as-text payload (issue #482).
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
                // Daily report creation uses the shared formatter so Nyx-relay slash commands and
                // Feishu card-action submits render the same "running now, I'll reply when done"
                // acknowledgment.
                DailyReportAction => AgentBuilderCardContent.FormatDailyReportToolReply(doc.RootElement),
                SocialMediaAction => FormatCreateSocialMediaResult(doc.RootElement),
                ListTemplatesAction => FormatListTemplatesResult(doc.RootElement),
                // Card-click "Refresh List" and the typed `/agents` command share the same
                // unified renderer (issue #476).
                ListAgentsAction => AgentBuilderCardContent.FormatListAgentsResult(doc.RootElement),
                AgentStatusAction => FormatAgentStatusResult(doc.RootElement),
                RunAgentAction => FormatRunAgentResult(doc.RootElement),
                DisableAgentAction => FormatDisableAgentResult(doc.RootElement),
                EnableAgentAction => FormatEnableAgentResult(doc.RootElement),
                // After a delete completes, surface the updated registry through the same unified
                // list renderer with the delete notice prepended.
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

    private static bool TryBuildCreateDailyReportArguments(
        ChannelInboundEvent evt,
        out string? argumentsJson,
        out string? validationError)
    {
        argumentsJson = null;
        validationError = null;
        var githubUsername = evt.Extra.TryGetValue("github_username", out var rawGithubUsername)
            ? NormalizeOptional(rawGithubUsername)
            : null;

        if (!TryBuildDailyCron(evt.Extra.TryGetValue("schedule_time", out var scheduleTime) ? scheduleTime : null, out var scheduleCron, out validationError))
            return false;

        var scheduleTimezone = (evt.Extra.TryGetValue("schedule_timezone", out var rawTimezone)
                ? rawTimezone
                : null) ?? SkillDefinitionDefaults.DefaultTimezone;
        scheduleTimezone = string.IsNullOrWhiteSpace(scheduleTimezone)
            ? SkillDefinitionDefaults.DefaultTimezone
            : scheduleTimezone.Trim();

        var repositories = evt.Extra.TryGetValue("repositories", out var rawRepositories)
            ? NormalizeOptional(rawRepositories)
            : null;

        var runImmediately = !evt.Extra.TryGetValue("run_immediately", out var rawRunImmediately) ||
                             !bool.TryParse(rawRunImmediately, out var parsedRunImmediately) ||
                             parsedRunImmediately;

        argumentsJson = JsonSerializer.Serialize(new
        {
            action = "create_agent",
            template = "daily_report",
            github_username = githubUsername,
            save_github_username_preference = githubUsername is not null,
            repositories,
            schedule_cron = scheduleCron,
            schedule_timezone = scheduleTimezone,
            run_immediately = runImmediately,
        });
        return true;
    }

    private static bool TryBuildCreateSocialMediaArguments(
        ChannelInboundEvent evt,
        out string? argumentsJson,
        out string? validationError)
    {
        argumentsJson = null;
        validationError = null;

        if (!TryGetRequiredExtra(evt, "topic", out var topic))
        {
            validationError = "Topic is required. Send /social-media and fill in the form again.";
            return false;
        }

        if (!TryBuildDailyCron(evt.Extra.TryGetValue("schedule_time", out var scheduleTime) ? scheduleTime : null, out var scheduleCron, out validationError))
            return false;

        var scheduleTimezone = (evt.Extra.TryGetValue("schedule_timezone", out var rawTimezone)
                ? rawTimezone
                : null) ?? SkillDefinitionDefaults.DefaultTimezone;
        scheduleTimezone = string.IsNullOrWhiteSpace(scheduleTimezone)
            ? SkillDefinitionDefaults.DefaultTimezone
            : scheduleTimezone.Trim();

        var audience = evt.Extra.TryGetValue("audience", out var rawAudience)
            ? NormalizeOptional(rawAudience)
            : null;
        var style = evt.Extra.TryGetValue("style", out var rawStyle)
            ? NormalizeOptional(rawStyle)
            : null;

        var runImmediately = !evt.Extra.TryGetValue("run_immediately", out var rawRunImmediately) ||
                             !bool.TryParse(rawRunImmediately, out var parsedRunImmediately) ||
                             parsedRunImmediately;

        argumentsJson = JsonSerializer.Serialize(new
        {
            action = "create_agent",
            template = "social_media",
            topic,
            audience,
            style,
            schedule_cron = scheduleCron,
            schedule_timezone = scheduleTimezone,
            run_immediately = runImmediately,
        });
        return true;
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

    private static bool TryBuildDailyCron(string? rawTime, out string? cron, out string? error)
    {
        cron = null;
        error = null;

        var normalized = NormalizeOptional(rawTime) ?? DefaultScheduleTime;
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

        cron = $"{time.Minute} {time.Hour} * * *";
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

    private static bool ShouldLoadPreferredGithubUsername(ChannelInboundEvent evt)
    {
        if (IsPrivateChatText(evt))
        {
            var normalized = NormalizeText(evt.Text);
            return LaunchIntents.Contains(normalized);
        }

        return string.Equals(evt.ChatType, CardActionChatType, StringComparison.Ordinal) &&
               evt.Extra.TryGetValue("agent_builder_action", out var action) &&
               string.Equals(action, OpenDailyReportFormAction, StringComparison.Ordinal);
    }

    private static string NormalizeText(string? text) => (text ?? string.Empty).Trim();

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static MessageContent FormatCreateSocialMediaResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return ToTextContent($"Create social media agent failed: {error}");

        var status = ReadString(root, "status") ?? "accepted";
        var agentId = ReadString(root, "agent_id") ?? "unknown-agent";
        var workflowId = ReadString(root, "workflow_id") ?? "pending";
        var nextRun = ReadString(root, "next_scheduled_run") ?? "pending";
        var note = NormalizeOptional(ReadString(root, "note"));

        var headline = string.Equals(status, "created", StringComparison.OrdinalIgnoreCase)
            ? "Social media agent created."
            : "Social media agent accepted.";

        var body = new StringBuilder();
        body.Append(headline).Append('\n');
        body.Append($"- Agent ID: `{agentId}`\n");
        body.Append($"- Workflow ID: `{workflowId}`\n");
        body.Append($"- Next scheduled run: `{nextRun}`");
        if (note is not null)
            body.Append("\n\n").Append(note);

        var content = new MessageContent();
        content.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = $"social_media_created:{agentId}",
            Title = "Social Media Agent",
            Text = body.ToString(),
        });
        content.Actions.Add(BuildCardAction("View Agents", ListAgentsAction, isPrimary: true));
        content.Actions.Add(BuildCardAction("Create Another", OpenSocialMediaFormAction, isPrimary: false));
        return content;
    }

    private static MessageContent FormatListTemplatesResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return ToTextContent($"List templates failed: {error}");

        var content = new MessageContent();

        if (!root.TryGetProperty("templates", out var templatesElement) ||
            templatesElement.ValueKind != JsonValueKind.Array ||
            templatesElement.GetArrayLength() == 0)
        {
            content.Cards.Add(new CardBlock
            {
                Kind = CardBlockKind.Section,
                BlockId = "templates_empty",
                Title = "Available Templates",
                Text = "No templates available right now.",
            });
            content.Actions.Add(BuildCardAction("View Agents", ListAgentsAction, isPrimary: false));
            return content;
        }

        var body = new StringBuilder();
        body.Append("Day One currently exposes the templates below.");

        var hasReadyDaily = false;
        var hasReadySocial = false;

        foreach (var item in templatesElement.EnumerateArray())
        {
            var name = ReadString(item, "name") ?? "unknown-template";
            var status = ReadString(item, "status") ?? "unknown";
            var description = ReadString(item, "description") ?? "No description.";
            var requiredFields = ReadStringArray(item, "required_fields");
            var optionalFields = ReadStringArray(item, "optional_fields");

            body.Append("\n\n");
            body.Append($"**`{name}`** · {status}\n");
            body.Append($"{description}\n");
            body.Append($"- Required: {FormatFieldList(requiredFields)}\n");
            body.Append($"- Optional: {FormatFieldList(optionalFields)}");

            if (string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(name, "daily_report", StringComparison.OrdinalIgnoreCase))
                    hasReadyDaily = true;
                else if (string.Equals(name, "social_media", StringComparison.OrdinalIgnoreCase))
                    hasReadySocial = true;
            }
        }

        content.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = "templates_list",
            Title = "Available Templates",
            Text = body.ToString(),
        });

        if (hasReadyDaily)
            content.Actions.Add(BuildCardAction("Create Daily Report", OpenDailyReportFormAction, isPrimary: true));
        if (hasReadySocial)
            content.Actions.Add(BuildCardAction("Create Social Media", OpenSocialMediaFormAction, isPrimary: !hasReadyDaily));
        content.Actions.Add(BuildCardAction("View Agents", ListAgentsAction, isPrimary: false));
        return content;
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
            SkillDefinitionDefaults.StatusDisabled,
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

        // The card-flow path keeps the explicit confirmation step before deletion (vs. the typed
        // /agent-status path's direct delete) so the per-agent template is carried along to the
        // confirmation card. Danger styling matches Lark's red-button affordance.
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

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                values.Add(item.GetString()!);
        }

        return values;
    }

    private static string FormatFieldList(IReadOnlyList<string> fields) =>
        fields.Count == 0
            ? "`None`"
            : string.Join(", ", fields.Select(static field => $"`{field}`"));

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
