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
    private const string CreateScheduledAgentAction = AgentBuilderActionIds.CreateScheduledAgent;
    private const string SubmitScheduledAgentAction = AgentBuilderActionIds.SubmitScheduledAgent;
    private const string ScheduledAgentCreatorToolName = ScheduledAgentCreatorTool.ToolName;
    private const string ListAgentsCommand = "/agents";
    private const string AgentStatusCommand = "/agent-status";
    private const string RunAgentCommand = "/run-agent";
    private const string DisableAgentCommand = "/disable-agent";
    private const string EnableAgentCommand = "/enable-agent";
    private const string DeleteAgentCommand = "/delete-agent";
    private const string CreateScheduledAgentCommand = "/create-scheduled-agent";

    private sealed record AgentBuilderCommandSpec(
        string TextCommand,
        string CardAction,
        string ToolAction,
        string Usage,
        bool RequiresAgentId,
        Func<IReadOnlyList<string>, AgentBuilderCommandSpec, AgentBuilderFlowDecision> BuildFromText,
        Func<ChannelInboundEvent, AgentBuilderCommandSpec, AgentBuilderFlowDecision> BuildFromCard,
        Func<JsonElement, MessageContent>? FormatResult);

    private static readonly AgentBuilderCommandSpec[] CommandSpecs =
    [
        new(
            ListAgentsCommand,
            ListAgentsAction,
            ListAgentsAction,
            string.Empty,
            RequiresAgentId: false,
            BuildListAgentsTextDecision,
            BuildListAgentsCardDecision,
            root => AgentBuilderCardContent.FormatListAgentsResult(root)),
        new(
            AgentStatusCommand,
            AgentStatusAction,
            AgentStatusAction,
            $"Usage: {AgentStatusCommand} <agent_id>",
            RequiresAgentId: true,
            BuildSimpleAgentTextDecision,
            BuildAgentScopedCardDecision,
            FormatAgentStatusResult),
        new(
            RunAgentCommand,
            RunAgentAction,
            RunAgentAction,
            $"Usage: {RunAgentCommand} <agent_id>",
            RequiresAgentId: true,
            BuildSimpleAgentTextDecision,
            BuildAgentScopedCardDecision,
            FormatRunAgentResult),
        new(
            DisableAgentCommand,
            DisableAgentAction,
            DisableAgentAction,
            $"Usage: {DisableAgentCommand} <agent_id>",
            RequiresAgentId: true,
            BuildSimpleAgentTextDecision,
            BuildAgentScopedCardDecision,
            FormatDisableAgentResult),
        new(
            EnableAgentCommand,
            EnableAgentAction,
            EnableAgentAction,
            $"Usage: {EnableAgentCommand} <agent_id>",
            RequiresAgentId: true,
            BuildSimpleAgentTextDecision,
            BuildAgentScopedCardDecision,
            FormatEnableAgentResult),
        new(
            DeleteAgentCommand,
            DeleteAgentAction,
            DeleteAgentAction,
            $"Usage: {DeleteAgentCommand} <agent_id>",
            RequiresAgentId: true,
            BuildDeleteAgentTextDecision,
            BuildDeleteAgentCardDecision,
            FormatDeleteAgentResultAsList),
        new(
            CreateScheduledAgentCommand,
            CreateScheduledAgentAction,
            CreateScheduledAgentAction,
            $"Usage: {CreateScheduledAgentCommand} [skill_ref]",
            RequiresAgentId: false,
            BuildCreateScheduledAgentTextDecision,
            BuildCreateScheduledAgentCardDecision,
            FormatResult: null),
    ];

    private static readonly AgentBuilderCommandSpec ConfirmDeleteSpec = new(
        TextCommand: string.Empty,
        CardAction: ConfirmDeleteAgentAction,
        ToolAction: ConfirmDeleteAgentAction,
        Usage: string.Empty,
        RequiresAgentId: true,
        BuildFromText: static (_, _) => throw new InvalidOperationException("confirm_delete_agent has no text command."),
        BuildFromCard: BuildConfirmDeleteCardDecision,
        FormatResult: null);

    private static readonly AgentBuilderCommandSpec SubmitScheduledAgentSpec = new(
        TextCommand: string.Empty,
        CardAction: SubmitScheduledAgentAction,
        ToolAction: ScheduledAgentCreatorToolName,
        Usage: string.Empty,
        RequiresAgentId: false,
        BuildFromText: static (_, _) => throw new InvalidOperationException("submit_scheduled_agent has no text command."),
        BuildFromCard: BuildSubmitScheduledAgentCardDecision,
        FormatResult: FormatScheduledAgentCreateResult);

    private static readonly IReadOnlyDictionary<string, AgentBuilderCommandSpec> SpecsByTextCommand =
        CommandSpecs.ToDictionary(static spec => spec.TextCommand, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, AgentBuilderCommandSpec> SpecsByCardAction =
        CommandSpecs
            .Append(ConfirmDeleteSpec)
            .Append(SubmitScheduledAgentSpec)
            .ToDictionary(static spec => spec.CardAction, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, AgentBuilderCommandSpec> SpecsByToolAction =
        CommandSpecs
            .Append(SubmitScheduledAgentSpec)
            .Where(static spec => spec.FormatResult is not null)
            .ToDictionary(static spec => spec.ToolAction, StringComparer.OrdinalIgnoreCase);

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

        if (TryResolveTextCommand(evt, out decision))
            return true;

        if (!string.Equals(evt.ChatType, CardActionChatType, StringComparison.Ordinal))
            return false;

        if (!evt.Extra.TryGetValue("agent_builder_action", out var action))
            return false;

        var normalizedAction = (action ?? string.Empty).Trim();
        if (!SpecsByCardAction.TryGetValue(normalizedAction, out var spec))
            return false;

        decision = spec.BuildFromCard(evt, spec);
        return true;
    }

    private static bool TryResolveTextCommand(
        ChannelInboundEvent evt,
        out AgentBuilderFlowDecision? decision)
    {
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
        if (!SpecsByTextCommand.TryGetValue(command, out var spec))
            return false;

        if (!IsPrivateChat(evt.ChatType))
        {
            decision = AgentBuilderFlowDecision.DirectReply(BuildPrivateChatRestrictionReply(command));
            return true;
        }

        decision = spec.BuildFromText(tokens, spec);
        return true;
    }

    private static AgentBuilderFlowDecision BuildListAgentsTextDecision(
        IReadOnlyList<string> tokens,
        AgentBuilderCommandSpec spec)
    {
        _ = tokens;
        return AgentBuilderFlowDecision.ToolCall(spec.ToolAction, JsonSerializer.Serialize(new
        {
            action = spec.ToolAction,
        }));
    }

    private static AgentBuilderFlowDecision BuildSimpleAgentTextDecision(
        IReadOnlyList<string> tokens,
        AgentBuilderCommandSpec spec)
    {
        if (tokens.Count < 2 || string.IsNullOrWhiteSpace(tokens[1]))
            return AgentBuilderFlowDecision.DirectReply(spec.Usage);

        return AgentBuilderFlowDecision.ToolCall(
            spec.ToolAction,
            JsonSerializer.Serialize(new
            {
                action = spec.ToolAction,
                agent_id = tokens[1].Trim(),
            }));
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
            return decision.ToolAction is not null &&
                   SpecsByToolAction.TryGetValue(decision.ToolAction, out var spec) &&
                   spec.FormatResult is not null
                ? spec.FormatResult(doc.RootElement)
                : ToTextContent(toolResultJson);
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

    private static AgentBuilderFlowDecision BuildListAgentsCardDecision(
        ChannelInboundEvent evt,
        AgentBuilderCommandSpec spec)
    {
        _ = evt;
        return AgentBuilderFlowDecision.ToolCall(spec.ToolAction, JsonSerializer.Serialize(new
        {
            action = spec.ToolAction,
        }));
    }

    private static AgentBuilderFlowDecision BuildAgentScopedCardDecision(
        ChannelInboundEvent evt,
        AgentBuilderCommandSpec spec)
    {
        if (!TryBuildAgentActionArguments(evt, spec, out var argumentsJson, out var validationError))
            return AgentBuilderFlowDecision.DirectReply(validationError!);

        return AgentBuilderFlowDecision.ToolCall(spec.ToolAction, argumentsJson!);
    }

    private static AgentBuilderFlowDecision BuildConfirmDeleteCardDecision(
        ChannelInboundEvent evt,
        AgentBuilderCommandSpec spec)
    {
        _ = spec;
        if (!TryGetRequiredExtra(evt, "agent_id", out var agentId))
            return AgentBuilderFlowDecision.DirectReply("agent_id is required for delete confirmation.");

        return AgentBuilderFlowDecision.DirectReply(BuildDeleteConfirmationCard(
            agentId,
            evt.Extra.TryGetValue("template", out var template) ? template : null));
    }

    private static AgentBuilderFlowDecision BuildDeleteAgentCardDecision(
        ChannelInboundEvent evt,
        AgentBuilderCommandSpec spec)
    {
        if (!TryBuildAgentActionArguments(evt, spec, out var argumentsJson, out var validationError, confirm: true))
            return AgentBuilderFlowDecision.DirectReply(validationError!);

        return AgentBuilderFlowDecision.ToolCall(spec.ToolAction, argumentsJson!);
    }

    private static AgentBuilderFlowDecision BuildCreateScheduledAgentTextDecision(
        IReadOnlyList<string> tokens,
        AgentBuilderCommandSpec spec)
    {
        _ = spec;
        var skillRef = tokens.Count > 1 ? NormalizeOptional(tokens[1]) : null;
        return AgentBuilderFlowDecision.DirectReply(BuildScheduledAgentCreateCard(skillRef));
    }

    private static AgentBuilderFlowDecision BuildCreateScheduledAgentCardDecision(
        ChannelInboundEvent evt,
        AgentBuilderCommandSpec spec)
    {
        _ = spec;
        var skillRef = evt.Extra.TryGetValue("skill_ref", out var rawSkillRef)
            ? NormalizeOptional(rawSkillRef)
            : null;
        return AgentBuilderFlowDecision.DirectReply(BuildScheduledAgentCreateCard(skillRef));
    }

    private static AgentBuilderFlowDecision BuildSubmitScheduledAgentCardDecision(
        ChannelInboundEvent evt,
        AgentBuilderCommandSpec spec)
    {
        if (!TryResolveScheduledAgentSubmit(evt, out var argumentsJson, out var validationError))
            return AgentBuilderFlowDecision.DirectReply(validationError!);

        return AgentBuilderFlowDecision.ToolCall(spec.ToolAction, argumentsJson!);
    }

    private static bool TryResolveScheduledAgentSubmit(
        ChannelInboundEvent evt,
        out string? argumentsJson,
        out string? validationError)
    {
        argumentsJson = null;
        validationError = null;

        var skillRef = ResolveScheduledAgentSkillRef(evt);
        if (skillRef is null)
        {
            validationError = "skill_ref is required. Enter an Ornn skill name and submit again.";
            return false;
        }

        if (!TryMapScheduleToCron(evt.Extra, out var cron, out validationError))
            return false;

        if (!TryResolveScheduleTimezone(evt.Extra, out var timezone, out validationError))
            return false;

        if (!TryResolveDeliveryTarget(evt.Extra, out validationError))
            return false;

        var outputFormat = ResolveOutputFormat(evt.Extra.TryGetValue("output_format", out var rawOutputFormat)
            ? rawOutputFormat
            : null);

        argumentsJson = JsonSerializer.Serialize(new
        {
            skill_ref = skillRef,
            schedule_cron = cron,
            schedule_timezone = timezone,
            output_format = outputFormat,
        });
        return true;
    }

    private static bool TryBuildAgentActionArguments(
        ChannelInboundEvent evt,
        AgentBuilderCommandSpec spec,
        out string? argumentsJson,
        out string? validationError,
        bool confirm = false)
    {
        argumentsJson = null;
        validationError = null;

        string? agentId = null;
        if (spec.RequiresAgentId && !TryGetRequiredExtra(evt, "agent_id", out agentId!))
        {
            validationError = "agent_id is required. Send /agents and retry from the latest card.";
            return false;
        }

        var revisionFeedback = string.Equals(spec.ToolAction, RunAgentAction, StringComparison.Ordinal)
            ? NormalizeOptional(evt.Extra.TryGetValue("revision_feedback", out var rawRevisionFeedback)
                ? rawRevisionFeedback
                : (evt.Extra.TryGetValue("user_input", out var rawUserInput) ? rawUserInput : null))
            : null;

        argumentsJson = JsonSerializer.Serialize(new
        {
            action = spec.ToolAction,
            agent_id = agentId,
            confirm,
            revision_feedback = revisionFeedback,
        });
        return true;
    }

    /// <summary>
    /// Parses <c>/delete-agent &lt;agent_id&gt; [confirm]</c>. Without the trailing keyword the
    /// card flow keeps its explicit confirmation card; with it, the command dispatches directly.
    /// </summary>
    private static AgentBuilderFlowDecision BuildDeleteAgentTextDecision(
        IReadOnlyList<string> tokens,
        AgentBuilderCommandSpec spec)
    {
        if (tokens.Count < 2 || string.IsNullOrWhiteSpace(tokens[1]))
            return AgentBuilderFlowDecision.DirectReply(spec.Usage);

        var agentId = tokens[1].Trim();
        var confirmed = tokens.Count > 2 &&
                        string.Equals(tokens[2], "confirm", StringComparison.OrdinalIgnoreCase);

        if (confirmed)
        {
            return AgentBuilderFlowDecision.ToolCall(
                spec.ToolAction,
                JsonSerializer.Serialize(new
                {
                    action = spec.ToolAction,
                    agent_id = agentId,
                    confirm = true,
                }));
        }

        return AgentBuilderFlowDecision.DirectReply(BuildDeleteConfirmationCard(agentId, null));
    }

    internal static bool TryMapScheduleToCron(
        IReadOnlyDictionary<string, string> fields,
        out string cron,
        out string? validationError)
    {
        cron = string.Empty;
        validationError = null;
        var frequency = NormalizeOptional(ReadField(fields, "frequency"))?.Replace('-', '_').ToLowerInvariant()
                        ?? "daily";

        if (frequency == "custom")
        {
            var customCron = NormalizeOptional(ReadField(fields, "custom_cron"));
            if (customCron is null)
            {
                validationError = "custom_cron is required when frequency is custom.";
                return false;
            }

            if (!LooksLikeFiveFieldCron(customCron))
            {
                validationError = "custom_cron must be a five-field cron expression.";
                return false;
            }

            cron = customCron;
            return true;
        }

        if (!TryParseScheduleTime(
                ReadField(fields, "schedule_time") ?? ReadField(fields, "time"),
                out var hour,
                out var minute,
                out validationError))
            return false;

        cron = frequency switch
        {
            "hourly" => $"{minute} * * * *",
            "weekly" => $"{minute} {hour} * * {ResolveWeekday(ReadField(fields, "weekday"))}",
            "daily" => $"{minute} {hour} * * *",
            _ => string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(cron))
            return true;

        validationError = "frequency must be daily, weekly, hourly, or custom.";
        return false;
    }

    internal static bool TryResolveScheduleTimezone(
        IReadOnlyDictionary<string, string> fields,
        out string timezone,
        out string? validationError)
    {
        validationError = null;
        timezone = NormalizeOptional(ReadField(fields, "schedule_timezone")) ??
                   NormalizeOptional(ReadField(fields, "timezone")) ??
                   "UTC";
        timezone = timezone switch
        {
            "utc" or "UTC" => "UTC",
            "shanghai" or "beijing" or "china" => "Asia/Shanghai",
            "singapore" => "Asia/Singapore",
            "new_york" or "new-york" => "America/New_York",
            "los_angeles" or "los-angeles" => "America/Los_Angeles",
            "london" => "Europe/London",
            _ => timezone,
        };

        if (timezone.Contains('/') || string.Equals(timezone, "UTC", StringComparison.Ordinal))
            return true;

        validationError = "schedule_timezone must be UTC or an IANA timezone such as Asia/Shanghai.";
        return false;
    }

    private static string? ResolveScheduledAgentSkillRef(ChannelInboundEvent evt) =>
        NormalizeOptional(ReadField(evt.Extra, "skill_ref"));

    private static bool TryResolveDeliveryTarget(
        IReadOnlyDictionary<string, string> fields,
        out string? validationError)
    {
        validationError = null;
        var deliveryTarget = NormalizeOptional(ReadField(fields, "delivery_target")) ?? "current_chat";
        if (string.Equals(deliveryTarget, "current_chat", StringComparison.OrdinalIgnoreCase))
            return true;

        validationError = "delivery_target currently supports current_chat only.";
        return false;
    }

    private static string ResolveOutputFormat(string? value)
    {
        var normalized = NormalizeOptional(value)?.Replace('-', '_').ToLowerInvariant();
        return normalized switch
        {
            "markdown" => "markdown",
            "json" => "json",
            _ => "plain_text",
        };
    }

    private static string ResolveWeekday(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant();
        return normalized switch
        {
            "sun" or "sunday" or "0" => "0",
            "tue" or "tuesday" or "2" => "2",
            "wed" or "wednesday" or "3" => "3",
            "thu" or "thursday" or "4" => "4",
            "fri" or "friday" or "5" => "5",
            "sat" or "saturday" or "6" => "6",
            _ => "1",
        };
    }

    private static bool TryParseScheduleTime(
        string? value,
        out int hour,
        out int minute,
        out string? validationError)
    {
        hour = 9;
        minute = 0;
        validationError = null;
        var normalized = NormalizeOptional(value) ?? "09:00";
        var parts = normalized.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out hour) ||
            !int.TryParse(parts[1], out minute) ||
            hour is < 0 or > 23 ||
            minute is < 0 or > 59)
        {
            validationError = "schedule_time must use HH:mm, for example 09:00.";
            return false;
        }

        return true;
    }

    private static bool LooksLikeFiveFieldCron(string cron) =>
        cron.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 5;

    private static string? ReadField(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : null;

    private static bool TryGetRequiredExtra(ChannelInboundEvent evt, string key, out string value)
    {
        value = string.Empty;
        if (!evt.Extra.TryGetValue(key, out var raw))
            return false;

        value = NormalizeOptional(raw) ?? string.Empty;
        return value.Length > 0;
    }

    private static bool IsPrivateChat(string? chatType) =>
        string.Equals(chatType, PrivateChatType, StringComparison.OrdinalIgnoreCase);

    private static string BuildPrivateChatRestrictionReply(string command) =>
        $"`{command}` only works in a private chat with this bot. Please DM me and run `{command}` again.";

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

    private static MessageContent FormatScheduledAgentCreateResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return ToTextContent($"Create scheduled agent failed: {error}");

        var status = ReadString(root, "status") ?? "accepted";
        var agentId = ReadString(root, "agent_id") ?? "pending";
        var note = ReadString(root, "note") ??
                   "Scheduled agent creation accepted. Use agent status after projection catches up.";

        var content = new MessageContent();
        content.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = $"scheduled_agent_created:{agentId}",
            Title = "Scheduled Agent Submitted",
            Text = $"Status: `{status}`\nAgent ID: `{agentId}`\n\n{note}",
        });
        content.Actions.Add(BuildAgentScopedCardAction("Refresh Status", AgentStatusAction, agentId, isPrimary: true));
        content.Actions.Add(BuildCardAction("Back to Agents", ListAgentsAction, isPrimary: false));
        return content;
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

    private static MessageContent BuildScheduledAgentCreateCard(string? skillRef)
    {
        var content = new MessageContent();
        content.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = "scheduled_agent_create",
            Title = "Create Scheduled Agent",
            Text = "Pick a schedule and output shape. Delivery uses this Lark chat.",
        });

        content.Actions.Add(BuildFormTextInput(
            "skill_ref",
            "Skill",
            "Ornn skill name",
            skillRef));
        content.Actions.Add(BuildSelectInput(
            "frequency",
            "Frequency",
            "Daily",
            "daily",
            ("Daily", "daily"),
            ("Weekly", "weekly"),
            ("Hourly", "hourly"),
            ("Custom cron", "custom")));
        content.Actions.Add(BuildFormTextInput(
            "schedule_time",
            "Time",
            "09:00",
            "09:00"));
        content.Actions.Add(BuildSelectInput(
            "weekday",
            "Weekday",
            "Monday",
            "1",
            ("Monday", "1"),
            ("Tuesday", "2"),
            ("Wednesday", "3"),
            ("Thursday", "4"),
            ("Friday", "5"),
            ("Saturday", "6"),
            ("Sunday", "0")));
        content.Actions.Add(BuildFormTextInput(
            "custom_cron",
            "Custom Cron",
            "0 9 * * *",
            null));
        content.Actions.Add(BuildSelectInput(
            "schedule_timezone",
            "Timezone",
            "UTC",
            "UTC",
            ("UTC", "UTC"),
            ("Asia/Shanghai", "Asia/Shanghai"),
            ("Asia/Singapore", "Asia/Singapore"),
            ("America/New_York", "America/New_York"),
            ("America/Los_Angeles", "America/Los_Angeles"),
            ("Europe/London", "Europe/London")));
        content.Actions.Add(BuildSelectInput(
            "delivery_target",
            "Delivery",
            "Current chat",
            "current_chat",
            ("Current chat", "current_chat")));
        content.Actions.Add(BuildSelectInput(
            "output_format",
            "Output Format",
            "Plain text",
            "plain_text",
            ("Plain text", "plain_text"),
            ("Markdown", "markdown"),
            ("JSON", "json")));

        var submit = new ActionElement
        {
            Kind = ActionElementKind.FormSubmit,
            ActionId = SubmitScheduledAgentAction,
            Label = "Create",
            IsPrimary = true,
        };
        submit.Arguments["agent_builder_action"] = SubmitScheduledAgentAction;
        content.Actions.Add(submit);

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

    private static ActionElement BuildFormTextInput(
        string actionId,
        string label,
        string placeholder,
        string? value) =>
        new()
        {
            Kind = ActionElementKind.TextInput,
            ActionId = actionId,
            Label = label,
            Placeholder = placeholder,
            Value = value ?? string.Empty,
        };

    private static ActionElement BuildSelectInput(
        string actionId,
        string label,
        string placeholder,
        string? defaultValue = null,
        params (string Label, string Value)[] options)
    {
        var select = FeishuCardHumanInteractionPort.BuildSelectInput(actionId, label, placeholder, options);
        select.Value = defaultValue ?? string.Empty;
        return select;
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
