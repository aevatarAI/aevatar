using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.Authoring.Lark;

/// <summary>
/// Builds channel-neutral <see cref="MessageContent"/> payloads for the Day One agent builder flow.
/// Actions and CardBlocks let the platform composer render native interactive cards instead of
/// bouncing a pre-serialized JSON blob through a plain-text fallback.
/// </summary>
public static class AgentBuilderCardContent
{
    private const string DailyReportAction = AgentBuilderActionIds.DailyReport;
    private const string SocialMediaAction = AgentBuilderActionIds.SocialMedia;
    private const string OpenDailyReportFormAction = AgentBuilderActionIds.OpenDailyReportForm;
    private const string OpenSocialMediaFormAction = AgentBuilderActionIds.OpenSocialMediaForm;
    private const string ListTemplatesAction = AgentBuilderActionIds.ListTemplates;
    private const string ListAgentsAction = AgentBuilderActionIds.ListAgents;
    private const string DefaultScheduleTime = "09:00";

    public static MessageContent BuildDailyReportForm(string? preferredGithubUsername) =>
        BuildDailyReportForm(preferredGithubUsername, introCard: null);

    /// <summary>
    /// Builds the Daily Report creation form card. When <paramref name="introCard"/> is null the
    /// default Day One description card is rendered; callers that need a different header (for
    /// example, the credentials-required re-prompt) pass their own <see cref="CardBlock"/> and this
    /// method uses it verbatim instead.
    /// </summary>
    public static MessageContent BuildDailyReportForm(
        string? preferredGithubUsername,
        CardBlock? introCard)
    {
        var normalizedSaved = string.IsNullOrWhiteSpace(preferredGithubUsername)
            ? null
            : preferredGithubUsername!.Trim();

        var content = new MessageContent();
        content.Cards.Add(introCard ?? BuildDefaultDailyReportIntroCard(normalizedSaved));

        // Pre-fill the saved GitHub username into the input's default_value so users see it inline
        // and can keep it with one submit click. Placeholder stays as a generic hint so the field
        // does not disappear when the user clicks to edit.
        var githubInput = BuildTextInput(
            "github_username",
            "GitHub Username",
            placeholder: "octocat");
        if (normalizedSaved is not null)
            githubInput.Value = normalizedSaved;
        content.Actions.Add(githubInput);

        content.Actions.Add(BuildTextInput(
            "repositories",
            "Repositories (Optional)",
            "owner/repo, owner/repo"));
        content.Actions.Add(BuildTextInput(
            "schedule_time",
            "Daily Time (HH:mm)",
            DefaultScheduleTime));
        content.Actions.Add(BuildTextInput(
            "schedule_timezone",
            "Time Zone",
            SkillRunnerDefaults.DefaultTimezone));

        var submit = BuildFormSubmit(
            "submit_daily_report",
            "Create Agent",
            isPrimary: true);
        submit.Arguments["agent_builder_action"] = DailyReportAction;
        submit.Arguments["run_immediately"] = "true";
        content.Actions.Add(submit);

        return content;
    }

    private static CardBlock BuildDefaultDailyReportIntroCard(string? savedGithubUsername)
    {
        var savedNote = savedGithubUsername is null
            ? string.Empty
            : $"\n\nSaved GitHub username: `{savedGithubUsername}` — it is already filled in, just press **Create Agent** to reuse it.";

        return new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = "daily_report_intro",
            Title = "Create Daily Report Agent",
            Text =
                "**Day One template:** Daily GitHub report\n" +
                "Fill in the fields below. The agent will run once now and then repeat every day at your chosen local time." +
                savedNote,
        };
    }

    public static MessageContent BuildSocialMediaForm()
    {
        var content = new MessageContent();
        content.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = "social_media_intro",
            Title = "Create Social Media Agent",
            Text =
                "**Workflow-backed template:** Social media draft + approval\n" +
                "Fill in the fields below. Each scheduled run will generate one draft and send approval instructions into this Feishu private chat.",
        });

        content.Actions.Add(BuildTextInput(
            "topic",
            "Topic",
            "Launch update for the new workflow feature"));
        content.Actions.Add(BuildTextInput(
            "audience",
            "Audience (Optional)",
            "Developers and technical founders"));
        content.Actions.Add(BuildTextInput(
            "style",
            "Style (Optional)",
            "Confident, concise, product-focused"));
        content.Actions.Add(BuildTextInput(
            "schedule_time",
            "Daily Time (HH:mm)",
            DefaultScheduleTime));
        content.Actions.Add(BuildTextInput(
            "schedule_timezone",
            "Time Zone",
            SkillRunnerDefaults.DefaultTimezone));

        var submit = BuildFormSubmit(
            "submit_social_media",
            "Create Agent",
            isPrimary: true);
        submit.Arguments["agent_builder_action"] = SocialMediaAction;
        submit.Arguments["run_immediately"] = "true";
        content.Actions.Add(submit);

        return content;
    }

    /// <summary>
    /// Builds the post-tool acknowledgment for the Day One daily report creation flow.
    /// The tool response returns GitHub username, preference-save status, and run_immediately trigger
    /// status, which this method folds into a short text reply that leads with "running now" when
    /// the schedule fired the first report, so the user knows a report is on the way.
    /// </summary>
    public static MessageContent FormatDailyReportToolReply(JsonElement root)
    {
        if (TryReadError(root, out var error))
            return TextContent($"Create daily report agent failed: {error}");

        var status = TryReadString(root, "status") ?? "accepted";
        if (string.Equals(status, "credentials_required", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "oauth_required", StringComparison.OrdinalIgnoreCase))
        {
            return BuildDailyReportCredentialsCard(root, status);
        }

        var agentId = TryReadString(root, "agent_id") ?? "unknown-agent";
        var githubUsername = TryReadString(root, "github_username");
        var savedPreference = TryReadBool(root, "github_username_preference_saved");
        // The tool reports whether it asked the skill-runner actor to run now, not whether the
        // runner actually finished — hence "requested", not "triggered". The ack text still says
        // "Running first report now" because we sent the command; if it fails downstream, the
        // ground-truth status surfaces through /agent-status, not through this immediate reply.
        var runImmediatelyRequested = TryReadBool(root, "run_immediately_requested");
        var nextRun = TryReadString(root, "next_scheduled_run") ?? "pending";

        var headline = runImmediatelyRequested
            ? (string.IsNullOrWhiteSpace(githubUsername)
                ? "Daily report scheduled. Running first report now — I'll reply with the results shortly."
                : $"Daily report scheduled for `{githubUsername}`. Running first report now — I'll reply with the results shortly.")
            : (string.IsNullOrWhiteSpace(githubUsername)
                ? "Daily report scheduled."
                : $"Daily report scheduled for `{githubUsername}`.");

        var lines = new List<string> { headline };
        if (savedPreference && !string.IsNullOrWhiteSpace(githubUsername))
            lines.Add($"Saved `{githubUsername}` as your default GitHub username.");

        lines.Add($"Next scheduled run: {nextRun}");
        lines.Add($"Agent ID: {agentId}");

        var note = TryReadOptional(root, "note");
        if (note is not null)
            lines.Add(note);

        lines.Add($"Next commands: /agents, /agent-status {agentId}, /run-agent {agentId}");

        return TextContent(string.Join('\n', lines));
    }

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
            emptyBody.Append("No agents yet. Create one to get started:\n");
            emptyBody.Append("- `/daily` — daily GitHub report\n");
            emptyBody.Append("- `/social-media` — social-media drafter\n\n");
            emptyBody.Append("Run `/templates` to browse all available templates.");

            content.Cards.Add(new CardBlock
            {
                Kind = CardBlockKind.Section,
                BlockId = "agents_empty",
                Title = "Your Agents",
                Text = emptyBody.ToString(),
            });
            content.Actions.Add(BuildAction("Create Daily Report", OpenDailyReportFormAction, isPrimary: true));
            content.Actions.Add(BuildAction("Create Social Media", OpenSocialMediaFormAction, isPrimary: false));
            content.Actions.Add(BuildAction("Templates", ListTemplatesAction, isPrimary: false));
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

            if (index > 1)
                bodyBuilder.Append("\n\n");

            bodyBuilder.Append($"**{index}. `{template}`** · {status}\n");
            bodyBuilder.Append($"- Agent ID: `{agentId}`\n");
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

        // Footer is intentionally limited to discovery / creation shortcuts. Per-agent actions
        // (status, run, disable, enable, delete) deliberately stay off this card to avoid the
        // visual "list + status panel" duplication called out in issue #476; the inline command
        // hints in the body cover the same ground without the layout noise.
        content.Actions.Add(BuildAction("Refresh", ListAgentsAction, isPrimary: false));
        content.Actions.Add(BuildAction("Templates", ListTemplatesAction, isPrimary: false));
        content.Actions.Add(BuildAction("Create Daily Report", OpenDailyReportFormAction, isPrimary: false));
        content.Actions.Add(BuildAction("Create Social Media", OpenSocialMediaFormAction, isPrimary: false));
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

    private static MessageContent BuildDailyReportCredentialsCard(JsonElement root, string status)
    {
        var providerId = TryReadString(root, "provider_id") ?? "unknown-provider";
        var url = TryReadString(root, "authorization_url")
                  ?? TryReadString(root, "auth_url")
                  ?? TryReadString(root, "url")
                  ?? TryReadString(root, "documentation_url");
        var note = TryReadString(root, "note")
                   ?? "Enter your GitHub username below — I'll save it as your default and run the report immediately.";
        var heading = string.Equals(status, "oauth_required", StringComparison.OrdinalIgnoreCase)
            ? "GitHub authorization required."
            : "GitHub credentials required.";

        var descriptionLines = new List<string>
        {
            $"**{heading}**",
            note,
            $"Provider ID: `{providerId}`",
        };
        if (!string.IsNullOrWhiteSpace(url))
            descriptionLines.Add($"Open: {url}");
        descriptionLines.Add("Or just reply with `/daily <github_username>` — I'll save it and run the report now.");

        var introCard = new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = "daily_report_credentials",
            Title = "Create Daily Report Agent",
            Text = string.Join('\n', descriptionLines),
        };

        var content = BuildDailyReportForm(preferredGithubUsername: null, introCard: introCard);

        // Plain-text fallback for channels that cannot render the card.
        var fallbackLines = new List<string>
        {
            heading,
            note,
            $"Provider ID: {providerId}",
        };
        if (!string.IsNullOrWhiteSpace(url))
            fallbackLines.Add($"Open: {url}");
        fallbackLines.Add("Reply with `/daily <github_username>` — I'll save it and run the report immediately.");

        content.Text = string.Join('\n', fallbackLines);
        return content;
    }

    private static ActionElement BuildTextInput(string actionId, string label, string placeholder) =>
        new()
        {
            Kind = ActionElementKind.TextInput,
            ActionId = actionId,
            Label = label,
            Placeholder = placeholder,
        };

    private static ActionElement BuildFormSubmit(string actionId, string label, bool isPrimary) =>
        new()
        {
            Kind = ActionElementKind.FormSubmit,
            ActionId = actionId,
            Label = label,
            IsPrimary = isPrimary,
        };

    private static MessageContent TextContent(string text) => AgentBuilderJson.TextContent(text);

    private static bool TryReadError(JsonElement root, out string error) =>
        AgentBuilderJson.TryReadError(root, out error);

    private static string? TryReadString(JsonElement element, string propertyName) =>
        AgentBuilderJson.TryReadString(element, propertyName);

    private static bool TryReadBool(JsonElement element, string propertyName) =>
        AgentBuilderJson.TryReadBool(element, propertyName);

    private static string? TryReadOptional(JsonElement element, string propertyName) =>
        AgentBuilderJson.TryReadOptional(element, propertyName);
}
