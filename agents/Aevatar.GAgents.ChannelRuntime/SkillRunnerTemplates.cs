using System.Text;

namespace Aevatar.GAgents.ChannelRuntime;

internal static class SkillRunnerTemplates
{
    public static IReadOnlyList<object> ListTemplates() =>
    [
        new
        {
            name = "daily_report",
            status = "ready",
            description = "Generate a daily GitHub progress summary and send it back to the current Feishu private chat.",
            required_fields = new[] { "github_username", "schedule_cron" },
            optional_fields = new[] { "repositories", "schedule_timezone", "run_immediately" },
        },
        new
        {
            name = "social_media",
            status = "planned",
            description = "Reserved for the workflow-based social approval flow in a later PR.",
            required_fields = Array.Empty<string>(),
            optional_fields = Array.Empty<string>(),
        },
    ];

    public static bool TryBuildDailyReportSpec(
        string githubUsername,
        string? repositories,
        out SkillRunnerTemplateSpec? spec,
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

        spec = new SkillRunnerTemplateSpec(
            "daily_report",
            "daily_report",
            skillPrompt,
            executionPrompt,
            ["api-github", "api-lark-bot"]);
        return true;
    }

    private static IReadOnlyList<string> NormalizeRepositories(string? repositories) =>
        (repositories ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

internal sealed record SkillRunnerTemplateSpec(
    string TemplateName,
    string SkillName,
    string SkillContent,
    string ExecutionPrompt,
    IReadOnlyList<string> RequiredServiceSlugs);
