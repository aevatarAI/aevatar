using System.Text;

namespace Aevatar.GAgents.ChannelRuntime;

internal static class AgentBuilderTemplates
{
    public static IReadOnlyList<object> ListTemplates() =>
    [
        new
        {
            name = "daily_report",
            status = "ready",
            description = "Generate a daily GitHub progress summary and send it back to the current Feishu private chat.",
            required_fields = new[] { "schedule_cron" },
            optional_fields = new[] { "github_username", "repositories", "schedule_timezone", "run_immediately" },
        },
        new
        {
            name = "social_media",
            status = "ready",
            description = "Generate a social media draft on a schedule and send it into the current Feishu private chat for approval.",
            required_fields = new[] { "topic", "schedule_cron" },
            optional_fields = new[] { "audience", "style", "schedule_timezone", "run_immediately" },
        },
    ];

    public static bool TryBuildDailyReportSpec(
        string githubUsername,
        string? repositories,
        out DailyReportTemplateSpec? spec,
        out string? error)
    {
        spec = null;
        error = null;

        var normalizedUser = (githubUsername ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedUser))
        {
            error = "github_username is required for template=daily_report";
            return false;
        }

        var repoList = NormalizeRepositories(repositories);
        var skillPrompt = BuildDailyReportSkillPrompt(normalizedUser, repoList);

        var executionPrompt = repoList.Count == 0
            ? $"Run the daily report for GitHub user `{normalizedUser}` covering the last 24 hours. Follow the section schema in the system prompt. Return plain text only."
            : $"Run the daily report for GitHub user `{normalizedUser}` covering the last 24 hours. Restrict source queries to these repositories (one pass per repo, do not collapse to a global search): {string.Join(", ", repoList)}. Follow the section schema in the system prompt. Return plain text only.";

        spec = new DailyReportTemplateSpec(
            "daily_report",
            "daily_report",
            skillPrompt,
            executionPrompt,
            ["api-github", "api-lark-bot"]);
        return true;
    }

    public static bool TryBuildSocialMediaSpec(
        string agentId,
        string topic,
        string? audience,
        string? style,
        out SocialMediaTemplateSpec? spec,
        out string? error)
    {
        spec = null;
        error = null;

        var normalizedAgentId = (agentId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedAgentId))
        {
            error = "agent_id is required for template=social_media";
            return false;
        }

        var normalizedTopic = (topic ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedTopic))
        {
            error = "topic is required for template=social_media";
            return false;
        }

        var normalizedAudience = NormalizeOptional(audience) ?? "general followers";
        var normalizedStyle = NormalizeOptional(style) ?? "clear, concise, and professional";
        var workflowId = BuildSocialMediaWorkflowId(normalizedAgentId);
        var workflowName = BuildSocialMediaWorkflowName(normalizedAgentId);
        var displayName = $"Social Media Approval {normalizedAgentId}";
        var executionPrompt = $"Generate the scheduled social media draft for topic `{normalizedTopic}` and route it for approval.";

        spec = new SocialMediaTemplateSpec(
            WorkflowId: workflowId,
            WorkflowName: workflowName,
            DisplayName: displayName,
            WorkflowYaml: BuildSocialMediaWorkflowYaml(
                workflowName,
                normalizedAgentId,
                normalizedTopic,
                normalizedAudience,
                normalizedStyle),
            ExecutionPrompt: executionPrompt);
        return true;
    }

    // Daily report system prompt is treated as a fetch-and-summarize SPECIFICATION rather than a
    // freeform creative brief: explicit section order, hard per-section line budgets, and an
    // "omit if empty" rule. See issue #423 for the rationale (current single-paragraph output is
    // too thin and pads when sources are silent).
    private static string BuildDailyReportSkillPrompt(string normalizedUser, IReadOnlyList<string> repoList)
    {
        var repoScope = repoList.Count == 0
            ? "Repository scope: not pinned. Use the global GitHub search endpoints listed below."
            : $"Repository scope: {string.Join(", ", repoList)}. Run the per-repo endpoints once per repo; do NOT fold the list into a global search query (the /search/* endpoints don't filter to a repo allowlist cleanly).";

        var prompt = new StringBuilder()
            .AppendLine("You are Aevatar Daily Report Runner.")
            .AppendLine("Each run produces one Feishu-ready summary of the user's recent GitHub work over the last 24 hours.")
            .AppendLine("Use NyxID-backed tools only. Prefer nyxid_proxy with service slug `api-github` for GitHub data access.")
            .AppendLine()
            .AppendLine($"Primary GitHub username: {normalizedUser}")
            .AppendLine(repoScope)
            .AppendLine()
            .AppendLine("# Output sections (emit in this exact order)")
            .AppendLine()
            .AppendLine("Each section has a hard line budget. If a section has zero data OR the source is unavailable, OMIT THE SECTION ENTIRELY (header and body) — do not pad with `no activity` or filler.")
            .AppendLine()
            .AppendLine("1. Title (1 line) — `Daily report — {username} — last 24h`.")
            .AppendLine("2. Shipped (≤6 lines) — PRs merged in the last 24h and commits to the default branch. Format `- [owner/repo#NNN] title`.")
            .AppendLine("3. In flight (≤6 lines) — open PRs authored by the user. Append `(stale)` when the PR has had no activity for >24h.")
            .AppendLine("4. Reviews (≤4 lines) — PRs the user reviewed in the window. Include kind counts, e.g. `approved 2 / requested-changes 1 / commented 3`.")
            .AppendLine("5. Issues (≤4 lines) — issues opened, closed, or commented on by the user.")
            .AppendLine("6. CI (≤3 lines) — failing builds on the default branch of the tracked repos (omit entirely when none are red).")
            .AppendLine("7. Trend (1 line, optional) — running totals vs the prior 24h, e.g. `Trend: shipped 3 (+1), reviews 5 (-2)`. Omit when the prior-window data could not be fetched.")
            .AppendLine("8. Blockers (1 line, always last) — `Blockers: <short list>` or `No blockers.` Auto-detect from: PRs >24h waiting on a review, CI red >2h, issues with labels `blocked` or `needs-info`.")
            .AppendLine()
            .AppendLine("If the entire 24h window has no measurable activity across all sources, return ONLY the title line followed by `No measurable activity in the last 24h.` and nothing else.")
            .AppendLine("Do not invent activity. Do not paraphrase issue or PR titles into different wording. Keep each line short — Feishu text messages have a body cap, prefer trimming trailing detail over exceeding it.")
            .AppendLine()
            .AppendLine("# Suggested GitHub proxy calls")
            .AppendLine();

        if (repoList.Count == 0)
        {
            prompt
                .AppendLine("Repository allowlist not provided — use the global search endpoints (substitute `{iso_date}` with the start of the 24h window in ISO 8601 UTC):")
                .AppendLine("- GET /search/issues?q=author:{username}+is:pr+is:merged+merged:>={iso_date}      // shipped PRs")
                .AppendLine("- GET /search/issues?q=author:{username}+is:pr+is:open                            // in flight")
                .AppendLine("- GET /search/issues?q=reviewed-by:{username}+updated:>={iso_date}                // reviews")
                .AppendLine("- GET /search/issues?q=author:{username}+is:issue+created:>={iso_date}            // issues opened")
                .AppendLine("- GET /search/issues?q=author:{username}+is:issue+is:closed+closed:>={iso_date}   // issues closed")
                .AppendLine("- GET /search/issues?q=commenter:{username}+updated:>={iso_date}                  // issues commented");
        }
        else
        {
            prompt
                .AppendLine("Repository allowlist provided — run these per-repo (replace `{owner}/{repo}` with each entry, substitute `{iso_date}` with the start of the 24h window in ISO 8601 UTC). Do NOT collapse into one global query.")
                .AppendLine("- GET /repos/{owner}/{repo}/pulls?state=closed&per_page=20               // filter merged_at >= {iso_date} client-side for shipped")
                .AppendLine("- GET /repos/{owner}/{repo}/pulls?state=open&per_page=20                 // filter author = {username} client-side for in flight")
                .AppendLine("- GET /search/issues?q=reviewed-by:{username}+repo:{owner}/{repo}+updated:>={iso_date}")
                .AppendLine("- GET /search/issues?q=author:{username}+repo:{owner}/{repo}+is:issue+updated:>={iso_date}")
                .AppendLine("- GET /repos/{owner}/{repo}/actions/runs?branch={default_branch}&per_page=10  // filter conclusion=failure for CI section");
        }

        prompt
            .AppendLine()
            .AppendLine("If a query returns 4xx, 5xx, or empty, treat that source as zero for the affected section and continue — do not retry, do not fall back to invented data.");

        return prompt.ToString();
    }

    private static string BuildSocialMediaWorkflowId(string agentId) =>
        $"social-media-{SanitizeSegment(agentId)}";

    private static string BuildSocialMediaWorkflowName(string agentId) =>
        $"social_media_{SanitizeSegment(agentId).Replace('-', '_')}";

    private static string BuildSocialMediaWorkflowYaml(
        string workflowName,
        string deliveryTargetId,
        string topic,
        string audience,
        string style)
    {
        return $$"""
            name: {{workflowName}}
            description: Generate a social media draft and request human approval in Feishu.

            roles:
              - id: writer
                name: Social Writer
                provider: nyxid
                system_prompt: |
                  You write polished short-form social media updates for professional audiences.
                  Keep drafts specific, concrete, and ready for human approval.

            steps:
              - id: draft_post
                type: llm_call
                role: writer
                parameters:
                  prompt_prefix: |
                    Draft one short social media post.
                    Topic: {{EscapeYamlBlock(topic)}}
                    Audience: {{EscapeYamlBlock(audience)}}
                    Style: {{EscapeYamlBlock(style)}}
                    Requirements:
                    - Return plain text only.
                    - Keep it concise and publication-ready.
                    - Do not add hashtags unless they are clearly justified.
                next: request_approval

              - id: request_approval
                type: human_approval
                parameters:
                  prompt: "Approve this social media draft?"
                  delivery_target_id: "{{EscapeDoubleQuoted(deliveryTargetId)}}"
                  on_reject: skip
                branches:
                  "true": done
                  "false": done

              - id: done
                type: assign
                parameters:
                  target: "result"
                  value: "$input"
            """;
    }

    private static string EscapeDoubleQuoted(string value) =>
        (value ?? string.Empty)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeYamlBlock(string value) =>
        (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static IReadOnlyList<string> NormalizeRepositories(string? repositories) =>
        (repositories ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string SanitizeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
            else if (ch is '-' or '_')
                builder.Append('-');
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "agent" : sanitized;
    }
}

internal sealed record DailyReportTemplateSpec(
    string TemplateName,
    string SkillName,
    string SkillContent,
    string ExecutionPrompt,
    IReadOnlyList<string> RequiredServiceSlugs);

internal sealed record SocialMediaTemplateSpec(
    string WorkflowId,
    string WorkflowName,
    string DisplayName,
    string WorkflowYaml,
    string ExecutionPrompt);
