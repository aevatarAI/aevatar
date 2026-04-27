using System.Text;

namespace Aevatar.GAgents.Authoring;

public static class AgentBuilderTemplates
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
        var repoConstraint = repoList.Count == 0
            ? "Search across the user's recent GitHub activity."
            : $"Focus on these repositories first: {string.Join(", ", repoList)}.";

        var skillPrompt = new StringBuilder()
            .AppendLine("You are Aevatar Daily Report Runner.")
            .AppendLine("Each run produces one concise Feishu-ready update for the user's recent work.")
            .AppendLine("Use NyxID-backed tools only. Prefer nyxid_proxy with service slug `api-github` for GitHub data access.")
            .AppendLine("Required output format:")
            .AppendLine("1. A short title line")
            .AppendLine("2. 3-6 concise bullet points")
            .AppendLine("3. One closing line with blockers or `No blockers.`")
            .AppendLine()
            .AppendLine($"Primary GitHub username: {normalizedUser}")
            .AppendLine(repoConstraint)
            .AppendLine("Suggested GitHub proxy calls:")
            .AppendLine("- GET /search/commits?q=author:{username}+author-date:>={iso_date}")
            .AppendLine("- GET /search/issues?q=author:{username}+updated:>={iso_date}")
            .AppendLine("- GET /search/issues?q=commenter:{username}+updated:>={iso_date}")
            .AppendLine("If there is no meaningful activity, say so plainly instead of inventing progress.")
            .ToString();

        var executionPrompt = repoList.Count == 0
            ? $"Run the daily report for GitHub user `{normalizedUser}` covering the last 24 hours. Return plain text only."
            : $"Run the daily report for GitHub user `{normalizedUser}` covering the last 24 hours. Prioritize repositories: {string.Join(", ", repoList)}. Return plain text only.";

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

public sealed record DailyReportTemplateSpec(
    string TemplateName,
    string SkillName,
    string SkillContent,
    string ExecutionPrompt,
    IReadOnlyList<string> RequiredServiceSlugs);

public sealed record SocialMediaTemplateSpec(
    string WorkflowId,
    string WorkflowName,
    string DisplayName,
    string WorkflowYaml,
    string ExecutionPrompt);
