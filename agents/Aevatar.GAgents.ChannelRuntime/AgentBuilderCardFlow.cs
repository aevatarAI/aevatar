using System.Globalization;
using System.Text.Json;

namespace Aevatar.GAgents.ChannelRuntime;

internal static class AgentBuilderCardFlow
{
    private const string PrivateChatType = "p2p";
    private const string CardActionChatType = "card_action";
    private const string DailyReportAction = "create_daily_report";
    private const string ListAgentsAction = "list_agents";
    private const string DefaultScheduleTime = "09:00";

    private static readonly HashSet<string> LaunchIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        "/daily-report",
        "/create-daily-report",
        "create daily report",
        "创建日报助手",
        "创建日报agent",
    };

    private static readonly HashSet<string> ListIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        "/agents",
        "list agents",
        "我的助手",
    };

    public static bool TryResolve(ChannelInboundEvent evt, out AgentBuilderFlowDecision? decision)
    {
        ArgumentNullException.ThrowIfNull(evt);
        decision = null;

        if (IsPrivateChatText(evt))
        {
            var normalized = NormalizeText(evt.Text);
            if (LaunchIntents.Contains(normalized))
            {
                decision = AgentBuilderFlowDecision.DirectReply(BuildDailyReportCard());
                return true;
            }

            if (ListIntents.Contains(normalized))
            {
                decision = AgentBuilderFlowDecision.ToolCall(ListAgentsAction, """{"action":"list_agents"}""");
                return true;
            }

            return false;
        }

        if (!string.Equals(evt.ChatType, CardActionChatType, StringComparison.Ordinal))
            return false;

        if (!evt.Extra.TryGetValue("agent_builder_action", out var action))
            return false;

        switch ((action ?? string.Empty).Trim())
        {
            case DailyReportAction:
                if (!TryBuildCreateDailyReportArguments(evt, out var argumentsJson, out var validationError))
                {
                    decision = AgentBuilderFlowDecision.DirectReply(validationError!);
                    return true;
                }

                decision = AgentBuilderFlowDecision.ToolCall(DailyReportAction, argumentsJson!);
                return true;

            case ListAgentsAction:
                decision = AgentBuilderFlowDecision.ToolCall(ListAgentsAction, """{"action":"list_agents"}""");
                return true;

            default:
                return false;
        }
    }

    public static string FormatToolResult(AgentBuilderFlowDecision decision, string toolResultJson)
    {
        ArgumentNullException.ThrowIfNull(decision);

        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            return decision.ToolAction switch
            {
                DailyReportAction => FormatCreateDailyReportResult(doc.RootElement),
                ListAgentsAction => FormatListAgentsResult(doc.RootElement),
                _ => toolResultJson,
            };
        }
        catch (JsonException)
        {
            return toolResultJson;
        }
    }

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

        if (!TryGetRequiredExtra(evt, "github_username", out var githubUsername))
        {
            validationError = "GitHub username is required. Send /daily-report and fill in the form again.";
            return false;
        }

        if (!TryBuildDailyCron(evt.Extra.TryGetValue("schedule_time", out var scheduleTime) ? scheduleTime : null, out var scheduleCron, out validationError))
            return false;

        var scheduleTimezone = (evt.Extra.TryGetValue("schedule_timezone", out var rawTimezone)
                ? rawTimezone
                : null) ?? SkillRunnerDefaults.DefaultTimezone;
        scheduleTimezone = string.IsNullOrWhiteSpace(scheduleTimezone)
            ? SkillRunnerDefaults.DefaultTimezone
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
            repositories,
            schedule_cron = scheduleCron,
            schedule_timezone = scheduleTimezone,
            run_immediately = runImmediately,
        });
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

    private static string NormalizeText(string? text) => (text ?? string.Empty).Trim();

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string BuildDailyReportCard()
    {
        return JsonSerializer.Serialize(new
        {
            config = new
            {
                wide_screen_mode = true,
            },
            header = new
            {
                title = new
                {
                    tag = "plain_text",
                    content = "Create Daily Report Agent",
                },
                template = "blue",
            },
            elements = new object[]
            {
                new
                {
                    tag = "markdown",
                    content =
                        "**Day One template:** Daily GitHub report\nFill in the fields below. The agent will run once now and then repeat every day at your chosen local time.",
                },
                BuildInput("github_username", "GitHub Username", "alice"),
                BuildInput("repositories", "Repositories (Optional)", "owner/repo, owner/repo"),
                BuildInput("schedule_time", "Daily Time (HH:mm)", DefaultScheduleTime),
                BuildInput("schedule_timezone", "Time Zone", SkillRunnerDefaults.DefaultTimezone),
                new
                {
                    tag = "action",
                    actions = new object[]
                    {
                        new
                        {
                            tag = "button",
                            type = "primary",
                            text = new
                            {
                                tag = "plain_text",
                                content = "Create Agent",
                            },
                            value = new
                            {
                                agent_builder_action = DailyReportAction,
                                run_immediately = true,
                            },
                        },
                        new
                        {
                            tag = "button",
                            type = "default",
                            text = new
                            {
                                tag = "plain_text",
                                content = "List Agents",
                            },
                            value = new
                            {
                                agent_builder_action = ListAgentsAction,
                            },
                        },
                    },
                },
            },
        });
    }

    private static object BuildInput(string name, string label, string placeholder)
    {
        return new
        {
            tag = "input",
            name,
            label = new
            {
                tag = "plain_text",
                content = label,
            },
            placeholder = new
            {
                tag = "plain_text",
                content = placeholder,
            },
        };
    }

    private static string FormatCreateDailyReportResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"Create daily report agent failed: {error}";

        var status = ReadString(root, "status") ?? "accepted";
        var agentId = ReadString(root, "agent_id") ?? "unknown-agent";
        var nextRun = ReadString(root, "next_scheduled_run") ?? "pending";
        var note = ReadString(root, "note");

        var lines = new List<string>
        {
            string.Equals(status, "created", StringComparison.OrdinalIgnoreCase)
                ? $"Daily report agent created: {agentId}"
                : $"Daily report agent accepted: {agentId}",
            $"Next scheduled run: {nextRun}",
        };

        if (!string.IsNullOrWhiteSpace(note))
            lines.Add(note!);

        lines.Add("Send /agents to view your current agents.");
        return string.Join("\n", lines);
    }

    private static string FormatListAgentsResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"List agents failed: {error}";

        if (!root.TryGetProperty("agents", out var agentsElement) ||
            agentsElement.ValueKind != JsonValueKind.Array)
        {
            return "No agents found. Send /daily-report to create one.";
        }

        var lines = new List<string>();
        foreach (var item in agentsElement.EnumerateArray())
        {
            var agentId = ReadString(item, "agent_id") ?? "unknown-agent";
            var template = ReadString(item, "template") ?? "unknown-template";
            var status = ReadString(item, "status") ?? "unknown";
            var nextRun = ReadString(item, "next_scheduled_run") ?? "pending";
            lines.Add($"- {agentId}: {template}, {status}, next {nextRun}");
        }

        return lines.Count == 0
            ? "No agents found. Send /daily-report to create one."
            : $"Current agents:\n{string.Join("\n", lines)}";
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
}

internal sealed record AgentBuilderFlowDecision(
    bool RequiresToolExecution,
    string ReplyPayload,
    string? ToolArgumentsJson,
    string? ToolAction)
{
    public static AgentBuilderFlowDecision DirectReply(string replyPayload) =>
        new(false, replyPayload, null, null);

    public static AgentBuilderFlowDecision ToolCall(string toolAction, string argumentsJson) =>
        new(true, string.Empty, argumentsJson, toolAction);
}
