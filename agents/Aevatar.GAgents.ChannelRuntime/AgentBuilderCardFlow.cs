using System.Globalization;
using System.Text.Json;

namespace Aevatar.GAgents.ChannelRuntime;

internal static class AgentBuilderCardFlow
{
    private const string PrivateChatType = "p2p";
    private const string CardActionChatType = "card_action";
    private const string OpenDailyReportFormAction = "open_daily_report_form";
    private const string DailyReportAction = "create_daily_report";
    private const string ListTemplatesAction = "list_templates";
    private const string ListAgentsAction = "list_agents";
    private const string AgentStatusAction = "agent_status";
    private const string RunAgentAction = "run_agent";
    private const string DisableAgentAction = "disable_agent";
    private const string EnableAgentAction = "enable_agent";
    private const string ConfirmDeleteAgentAction = "confirm_delete_agent";
    private const string DeleteAgentAction = "delete_agent";
    private const string DefaultScheduleTime = "09:00";
    private const string AgentStatusCommand = "/agent-status";
    private const string RunAgentCommand = "/run-agent";
    private const string DisableAgentCommand = "/disable-agent";
    private const string EnableAgentCommand = "/enable-agent";
    private const string DeleteAgentCommand = "/delete-agent";

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

    private static readonly HashSet<string> TemplateIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        "/templates",
        "/agent-templates",
        "list templates",
        "模板列表",
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
                decision = AgentBuilderFlowDecision.DirectReply(BuildDailyReportCard());
                return true;

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

    public static string FormatToolResult(AgentBuilderFlowDecision decision, string toolResultJson)
    {
        ArgumentNullException.ThrowIfNull(decision);

        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            return decision.ToolAction switch
            {
                DailyReportAction => FormatCreateDailyReportResult(doc.RootElement),
                ListTemplatesAction => FormatListTemplatesResult(doc.RootElement),
                ListAgentsAction => FormatListAgentsResult(doc.RootElement),
                AgentStatusAction => FormatAgentStatusResult(doc.RootElement),
                RunAgentAction => FormatRunAgentResult(doc.RootElement),
                DisableAgentAction => FormatDisableAgentResult(doc.RootElement),
                EnableAgentAction => FormatEnableAgentResult(doc.RootElement),
                DeleteAgentAction => FormatDeleteAgentResult(doc.RootElement),
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

        argumentsJson = JsonSerializer.Serialize(new
        {
            action,
            agent_id = agentId,
            confirm,
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

        if (TryParseAgentCommand(normalizedText, DeleteAgentCommand, out agentId, out errorReply))
        {
            if (errorReply != null)
            {
                decision = AgentBuilderFlowDecision.DirectReply(errorReply);
                return true;
            }

            decision = AgentBuilderFlowDecision.DirectReply(BuildDeleteConfirmationCard(agentId!, null));
            return true;
        }

        return false;
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

        return BuildInfoCard(
            "Daily Report Agent",
            string.Join("\n", lines),
            "green",
            new object[]
            {
                BuildButton("View Agents", "primary", new
                {
                    agent_builder_action = ListAgentsAction,
                }),
                BuildButton("Create Another", "default", new
                {
                    agent_builder_action = OpenDailyReportFormAction,
                }),
            });
    }

    private static string FormatListTemplatesResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"List templates failed: {error}";

        if (!root.TryGetProperty("templates", out var templatesElement) ||
            templatesElement.ValueKind != JsonValueKind.Array)
        {
            return "No templates available.";
        }

        var elements = new List<object>
        {
            new
            {
                tag = "markdown",
                content = "Day One currently exposes the templates below.",
            },
        };

        foreach (var item in templatesElement.EnumerateArray())
        {
            var name = ReadString(item, "name") ?? "unknown-template";
            var status = ReadString(item, "status") ?? "unknown";
            var description = ReadString(item, "description") ?? "No description.";
            var requiredFields = ReadStringArray(item, "required_fields");
            var optionalFields = ReadStringArray(item, "optional_fields");

            elements.Add(new
            {
                tag = "markdown",
                content =
                    $"**{EscapeMarkdown(name)}**\nStatus: `{EscapeMarkdown(status)}`\n{EscapeMarkdown(description)}\nRequired: {FormatFieldList(requiredFields)}\nOptional: {FormatFieldList(optionalFields)}",
            });

            if (string.Equals(name, "daily_report", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                elements.Add(new
                {
                    tag = "action",
                    actions = new object[]
                    {
                        BuildButton("Create Daily Report", "primary", new
                        {
                            agent_builder_action = OpenDailyReportFormAction,
                        }),
                    },
                });
            }
        }

        elements.Add(new
        {
            tag = "action",
            actions = new object[]
            {
                BuildButton("List Agents", "default", new
                {
                    agent_builder_action = ListAgentsAction,
                }),
            },
        });

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
                    content = "Available Templates",
                },
                template = "indigo",
            },
            elements,
        });
    }

    private static string FormatListAgentsResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"List agents failed: {error}";

        if (!root.TryGetProperty("agents", out var agentsElement) ||
            agentsElement.ValueKind != JsonValueKind.Array)
        {
            return BuildEmptyAgentListCard();
        }

        var agents = new List<AgentListCardItem>();
        foreach (var item in agentsElement.EnumerateArray())
        {
            var agentId = ReadString(item, "agent_id") ?? "unknown-agent";
            var template = ReadString(item, "template") ?? "unknown-template";
            var status = ReadString(item, "status") ?? "unknown";
            var nextRun = ReadString(item, "next_scheduled_run") ?? "pending";
            agents.Add(new AgentListCardItem(agentId, template, status, nextRun));
        }

        return agents.Count == 0
            ? BuildEmptyAgentListCard()
            : BuildAgentListCard(agents);
    }

    private static string FormatAgentStatusResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"Agent status failed: {error}";

        var agentId = ReadString(root, "agent_id") ?? "unknown-agent";
        var template = ReadString(root, "template") ?? "unknown-template";
        var status = ReadString(root, "status") ?? "unknown";
        var scheduleCron = ReadString(root, "schedule_cron") ?? "n/a";
        var scheduleTimezone = ReadString(root, "schedule_timezone") ?? "n/a";
        var lastRunAt = ReadString(root, "last_run_at") ?? "n/a";
        var nextRunAt = ReadString(root, "next_scheduled_run") ?? "n/a";
        var errorCount = ReadString(root, "error_count") ?? "0";
        var lastError = ReadString(root, "last_error");
        var note = ReadString(root, "note");

        var lines = new List<string>
        {
            $"**Agent:** `{agentId}`",
            $"Template: `{template}`",
            $"Status: `{status}`",
            $"Schedule: `{scheduleCron}` ({scheduleTimezone})",
            $"Last run: `{lastRunAt}`",
            $"Next run: `{nextRunAt}`",
            $"Error count: `{errorCount}`",
        };

        if (!string.IsNullOrWhiteSpace(lastError))
            lines.Add($"Last error: `{lastError}`");
        if (!string.IsNullOrWhiteSpace(note))
            lines.Add(note!);

        return BuildInfoCard(
            "Agent Status",
            string.Join("\n", lines),
            string.Equals(status, SkillRunnerDefaults.StatusDisabled, StringComparison.OrdinalIgnoreCase) ? "grey" : "green",
            BuildStatusCardActions(agentId, template, status));
    }

    private static string FormatRunAgentResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"Run agent failed: {error}";

        var agentId = ReadString(root, "agent_id") ?? "unknown-agent";
        var template = ReadString(root, "template") ?? "unknown-template";
        var note = ReadString(root, "note") ?? "Manual run dispatched.";

        return BuildInfoCard(
            "Run Triggered",
            $"Agent `{agentId}` (`{template}`)\n{note}",
            "green",
            new object[]
            {
                BuildButton("Back to Agents", "default", new
                {
                    agent_builder_action = ListAgentsAction,
                }),
                BuildButton("Refresh Status", "primary", new
                {
                    agent_builder_action = AgentStatusAction,
                    agent_id = agentId,
                }),
            });
    }

    private static string FormatDisableAgentResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"Disable agent failed: {error}";

        return FormatAgentStatusResult(root);
    }

    private static string FormatEnableAgentResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"Enable agent failed: {error}";

        return FormatAgentStatusResult(root);
    }

    private static string FormatDeleteAgentResult(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return $"Delete agent failed: {error}";

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

        var noticeMarkdown = string.Join("\n", lines);
        var agents = ReadAgentList(root);
        return agents.Count == 0
            ? BuildEmptyAgentListCard(noticeMarkdown)
            : BuildAgentListCard(agents, noticeMarkdown);
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

    private static string BuildAgentListCard(IReadOnlyList<AgentListCardItem> agents, string? noticeMarkdown = null)
    {
        var elements = new List<object>
        {
            new
            {
                tag = "markdown",
                content = $"You currently have **{agents.Count}** agent(s).",
            },
            new
            {
                tag = "markdown",
                content = "Quick commands: `/agent-status <agent_id>`, `/run-agent <agent_id>`, `/disable-agent <agent_id>`, `/enable-agent <agent_id>`, `/delete-agent <agent_id>`",
            },
        };

        if (!string.IsNullOrWhiteSpace(noticeMarkdown))
        {
            elements.Insert(0, new
            {
                tag = "markdown",
                content = noticeMarkdown,
            });
        }

        foreach (var agent in agents)
        {
            elements.Add(new
            {
                tag = "markdown",
                content = $"**{EscapeMarkdown(agent.Template)}**\nID: `{EscapeMarkdown(agent.AgentId)}`\nStatus: `{EscapeMarkdown(agent.Status)}`\nNext run: `{EscapeMarkdown(agent.NextRun)}`",
            });
            elements.Add(new
            {
                tag = "action",
                actions = new object[]
                {
                    BuildButton("Status", "primary", new
                    {
                        agent_builder_action = AgentStatusAction,
                        agent_id = agent.AgentId,
                    }),
                    BuildAgentListPrimaryAction(agent),
                    BuildButton("Delete", "danger", new
                    {
                        agent_builder_action = ConfirmDeleteAgentAction,
                        agent_id = agent.AgentId,
                        template = agent.Template,
                    }),
                },
            });
        }

        elements.Add(new
        {
            tag = "action",
            actions = new object[]
            {
                BuildButton("Refresh List", "default", new
                {
                    agent_builder_action = ListAgentsAction,
                }),
                BuildButton("Create Daily Report", "default", new
                {
                    agent_builder_action = OpenDailyReportFormAction,
                }),
                BuildButton("View Templates", "default", new
                {
                    agent_builder_action = ListTemplatesAction,
                }),
            },
        });

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
                    content = "Current Agents",
                },
                template = "wathet",
            },
            elements,
        });
    }

    private static string BuildEmptyAgentListCard()
    {
        return BuildEmptyAgentListCard(null);
    }

    private static string BuildEmptyAgentListCard(string? noticeMarkdown)
    {
        var elements = new List<object>();
        if (!string.IsNullOrWhiteSpace(noticeMarkdown))
        {
            elements.Add(new
            {
                tag = "markdown",
                content = noticeMarkdown,
            });
        }

        elements.Add(new
        {
            tag = "markdown",
            content = "No agents found yet. Create your first daily report agent from here.",
        });
        elements.Add(new
        {
            tag = "markdown",
            content = "Quick commands: `/templates`, `/daily-report`, `/agent-status <agent_id>`",
        });
        elements.Add(new
        {
            tag = "action",
            actions = new object[]
            {
                BuildButton("Create Daily Report", "primary", new
                {
                    agent_builder_action = OpenDailyReportFormAction,
                }),
                BuildButton("View Templates", "default", new
                {
                    agent_builder_action = ListTemplatesAction,
                }),
            },
        });

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
                    content = "Current Agents",
                },
                template = "wathet",
            },
            elements,
        });
    }

    private static string BuildDeleteConfirmationCard(string agentId, string? template)
    {
        var templateLabel = NormalizeOptional(template) ?? "unknown-template";
        return BuildInfoCard(
            "Delete Agent",
            $"Delete agent `{EscapeMarkdown(agentId)}` from template `{EscapeMarkdown(templateLabel)}`?\nThis will disable scheduling, revoke the Nyx API key, and tombstone the registry entry.",
            "red",
            new object[]
            {
                BuildButton("Confirm Delete", "danger", new
                {
                    agent_builder_action = DeleteAgentAction,
                    agent_id = agentId,
                }),
                BuildButton("Back to Agents", "default", new
                {
                    agent_builder_action = ListAgentsAction,
                }),
            });
    }

    private static string BuildInfoCard(
        string title,
        string markdown,
        string template,
        object[] actions)
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
                    content = title,
                },
                template,
            },
            elements = new object[]
            {
                new
                {
                    tag = "markdown",
                    content = markdown,
                },
                new
                {
                    tag = "action",
                    actions,
                },
            },
        });
    }

    private static object BuildButton(string label, string style, object value) =>
        new
        {
            tag = "button",
            type = style,
            text = new
            {
                tag = "plain_text",
                content = label,
            },
            value,
        };

    private static string EscapeMarkdown(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private static object[] BuildStatusCardActions(string agentId, string template, string status)
    {
        var actions = new List<object>
        {
            BuildButton("Refresh Status", "default", new
            {
                agent_builder_action = AgentStatusAction,
                agent_id = agentId,
            }),
        };

        if (string.Equals(status, SkillRunnerDefaults.StatusDisabled, StringComparison.OrdinalIgnoreCase))
        {
            actions.Add(BuildButton("Enable Agent", "primary", new
            {
                agent_builder_action = EnableAgentAction,
                agent_id = agentId,
            }));
        }
        else
        {
            actions.Add(BuildButton("Run Now", "primary", new
            {
                agent_builder_action = RunAgentAction,
                agent_id = agentId,
            }));
            actions.Add(BuildButton("Disable Agent", "default", new
            {
                agent_builder_action = DisableAgentAction,
                agent_id = agentId,
            }));
        }

        actions.Add(BuildButton("Back to Agents", "default", new
        {
            agent_builder_action = ListAgentsAction,
        }));
        actions.Add(BuildButton("Delete Agent", "danger", new
        {
            agent_builder_action = ConfirmDeleteAgentAction,
            agent_id = agentId,
            template,
        }));

        return actions.ToArray();
    }

    private static object BuildAgentListPrimaryAction(AgentListCardItem agent)
    {
        if (string.Equals(agent.Status, SkillRunnerDefaults.StatusDisabled, StringComparison.OrdinalIgnoreCase))
        {
            return BuildButton("Enable", "default", new
            {
                agent_builder_action = EnableAgentAction,
                agent_id = agent.AgentId,
            });
        }

        return BuildButton("Run Now", "default", new
        {
            agent_builder_action = RunAgentAction,
            agent_id = agent.AgentId,
        });
    }

    private static IReadOnlyList<AgentListCardItem> ReadAgentList(JsonElement root)
    {
        if (!root.TryGetProperty("agents", out var agentsElement) ||
            agentsElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<AgentListCardItem>();

        var agents = new List<AgentListCardItem>();
        foreach (var item in agentsElement.EnumerateArray())
        {
            var agentId = ReadString(item, "agent_id") ?? "unknown-agent";
            var template = ReadString(item, "template") ?? "unknown-template";
            var status = ReadString(item, "status") ?? "unknown";
            var nextRun = ReadString(item, "next_scheduled_run") ?? "pending";
            agents.Add(new AgentListCardItem(agentId, template, status, nextRun));
        }

        return agents;
    }
}

internal sealed record AgentListCardItem(
    string AgentId,
    string Template,
    string Status,
    string NextRun);

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
