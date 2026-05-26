using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Chat;

internal static class SkillRecoveryFinalAnswerGuard
{
    private const string OrnnSearchSkillsToolName = "ornn_search_skills";
    private const string UseSkillToolName = "use_skill";

    public readonly record struct RecoveryDirective(
        ToolCall? ToolCall,
        bool ConsumesOrnnSearchAttempt,
        string? Nudge);

    private static readonly string[] BlockerPhrases =
    [
        "error",
        "failed",
        "failure",
        "exception",
        "timeout",
        "timed out",
        "unavailable",
        "not available",
        "not configured",
        "missing",
        "invalid uri",
        "could not",
        "couldn't",
        "cannot",
        "can't",
        "blocked",
        "blocker",
        "no nyxid access token",
        "search failed",
        "not found",
        "skipped due to prior tool error",
        "tool execution was discarded",
        "backend unavailable",
    ];

    private static readonly string[] FinalFailurePhrases =
    [
        "无法",
        "失败",
        "不可用",
        "未配置",
        "缺少",
        "报错",
        "错误",
        "不能完成",
        "不能生成",
        "could not",
        "couldn't",
        "cannot",
        "can't",
        "failed",
        "failure",
        "error",
        "unavailable",
        "not configured",
        "missing",
        "blocked",
        "blocker",
    ];

    private static readonly string[] OrnnSearchNoMatchPhrases =
    [
        "no skills found",
        "search failed",
        "error:",
        "no nyxid access token",
        "not reachable",
        "not available",
        "unavailable",
    ];

    public static bool ShouldForceRecovery(
        AgentSkillRecoveryContext recovery,
        IReadOnlyList<ChatMessage> messages,
        string? finalContent,
        int recoveryAttempts,
        string? callIdPrefix,
        out RecoveryDirective directive)
    {
        directive = default;
        if (!IsEnabled(recovery))
            return false;

        var maxAttempts = recovery.MaxOrnnSearchAttempts > 0
            ? recovery.MaxOrnnSearchAttempts
            : 1;

        if (recovery.RequireInitialOrnnSearch &&
            !HasToolCall(messages, OrnnSearchSkillsToolName))
        {
            if (recoveryAttempts >= maxAttempts)
                return false;

            directive = new RecoveryDirective(
                BuildOrnnSearchToolCall(BuildCallId(callIdPrefix, OrnnSearchSkillsToolName), BuildInitialSearchQuery(recovery)),
                ConsumesOrnnSearchAttempt: true,
                Nudge: null);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(recovery.PrimarySkillName) &&
            !HasUseSkillFor(messages, recovery.PrimarySkillName))
        {
            directive = new RecoveryDirective(
                BuildUseSkillToolCall(
                    BuildCallId(callIdPrefix, UseSkillToolName),
                    recovery.PrimarySkillName,
                    ExtractCommandArguments(recovery)),
                ConsumesOrnnSearchAttempt: false,
                Nudge: null);
            return true;
        }

        if (TryGetLatestOrnnSearchWithMatches(messages, out var latestSearchIndex, out var latestSearchResult) &&
            !HasToolCallAfter(messages, UseSkillToolName, latestSearchIndex))
        {
            var skillName = ExtractFirstSkillName(latestSearchResult);
            directive = !string.IsNullOrWhiteSpace(skillName)
                ? new RecoveryDirective(
                    BuildUseSkillToolCall(
                        BuildCallId(callIdPrefix, UseSkillToolName),
                        skillName,
                        ExtractCommandArguments(recovery)),
                    ConsumesOrnnSearchAttempt: false,
                    Nudge: null)
                : new RecoveryDirective(
                    ToolCall: null,
                    ConsumesOrnnSearchAttempt: false,
                    Nudge: BuildUseDiscoveredSkillNudge(recovery, latestSearchResult));
            return true;
        }

        if (!recovery.RequireOrnnSearchOnBlocker)
            return false;

        if (!HasToolCall(messages, UseSkillToolName))
            return false;

        if (!HasBlockerAfterLastOrnnSearch(messages, finalContent))
            return false;

        if (recoveryAttempts >= maxAttempts)
            return false;

        var blocker = SummarizeBlocker(messages, finalContent);
        directive = new RecoveryDirective(
            BuildOrnnSearchToolCall(BuildCallId(callIdPrefix, OrnnSearchSkillsToolName), blocker),
            ConsumesOrnnSearchAttempt: true,
            Nudge: null);
        return true;
    }

    private static ToolCall BuildOrnnSearchToolCall(string callId, string query) =>
        new()
        {
            Id = callId,
            Name = OrnnSearchSkillsToolName,
            ArgumentsJson = JsonSerializer.Serialize(new
            {
                query,
                scope = "mixed",
            }),
        };

    private static ToolCall BuildUseSkillToolCall(string callId, string skillName, string args) =>
        new()
        {
            Id = callId,
            Name = UseSkillToolName,
            ArgumentsJson = JsonSerializer.Serialize(new
            {
                skill = skillName,
                args,
            }),
        };

    private static string BuildCallId(string? callIdPrefix, string toolName)
    {
        var suffix = toolName.Replace('_', '-');
        return string.IsNullOrWhiteSpace(callIdPrefix)
            ? $"skill-recovery:{suffix}"
            : $"{callIdPrefix}:skill-recovery:{suffix}";
    }

    private static bool IsEnabled(AgentSkillRecoveryContext recovery) =>
        recovery.RequireInitialOrnnSearch || recovery.RequireOrnnSearchOnBlocker;

    private static bool HasToolCall(IReadOnlyList<ChatMessage> messages, string toolName) =>
        messages.Any(message =>
            message.ToolCalls?.Any(call => IsTool(call, toolName)) == true);

    private static bool HasToolCallAfter(IReadOnlyList<ChatMessage> messages, string toolName, int startIndex)
    {
        for (var i = Math.Max(0, startIndex + 1); i < messages.Count; i++)
        {
            if (messages[i].ToolCalls?.Any(call => IsTool(call, toolName)) == true)
                return true;
        }

        return false;
    }

    private static bool HasUseSkillFor(IReadOnlyList<ChatMessage> messages, string skillName) =>
        messages.Any(message =>
            message.ToolCalls?.Any(call => IsTool(call, UseSkillToolName) && ToolCallUsesSkill(call, skillName)) == true);

    private static bool ToolCallUsesSkill(ToolCall call, string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
            return false;

        var arguments = call.ArgumentsJson;
        if (string.IsNullOrWhiteSpace(arguments))
            return false;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("skill", out var skillProperty) &&
                skillProperty.ValueKind == JsonValueKind.String)
            {
                return string.Equals(
                    skillProperty.GetString()?.Trim(),
                    skillName.Trim(),
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            // Some providers still emit best-effort JSON-like arguments. Fall through to a
            // conservative substring match so the guard does not keep nudging after the
            // intended skill was visibly requested.
        }

        return arguments.Contains(skillName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetLatestOrnnSearchWithMatches(
        IReadOnlyList<ChatMessage> messages,
        out int searchMessageIndex,
        out string searchResult)
    {
        searchMessageIndex = -1;
        searchResult = string.Empty;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var calls = messages[i].ToolCalls;
            if (calls is not { Count: > 0 })
                continue;

            for (var j = calls.Count - 1; j >= 0; j--)
            {
                var call = calls[j];
                if (!IsTool(call, OrnnSearchSkillsToolName))
                    continue;

                searchMessageIndex = i;
                searchResult = FindToolResult(messages, call.Id, i + 1);
                return SearchResultHasMatches(searchResult);
            }
        }

        return false;
    }

    private static string FindToolResult(IReadOnlyList<ChatMessage> messages, string? callId, int startIndex)
    {
        if (string.IsNullOrWhiteSpace(callId))
            return string.Empty;

        for (var i = Math.Max(0, startIndex); i < messages.Count; i++)
        {
            var message = messages[i];
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(message.ToolCallId, callId, StringComparison.Ordinal))
            {
                return message.Content ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool SearchResultHasMatches(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return false;

        if (ContainsAny(result, OrnnSearchNoMatchPhrases))
            return false;

        return result.Contains("Found ", StringComparison.OrdinalIgnoreCase) &&
               result.Contains("skill", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasBlockerAfterLastOrnnSearch(IReadOnlyList<ChatMessage> messages, string? finalContent)
    {
        var start = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].ToolCalls?.Any(call => IsTool(call, OrnnSearchSkillsToolName)) == true)
            {
                start = i + 1;
                break;
            }
        }

        for (var i = start; i < messages.Count; i++)
        {
            var message = messages[i];
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                ContainsAny(message.Content, BlockerPhrases))
            {
                return true;
            }
        }

        return ContainsAny(finalContent, FinalFailurePhrases);
    }

    private static bool ContainsAny(string? value, IReadOnlyList<string> phrases)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var phrase in phrases)
        {
            if (value.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string BuildInitialSearchNudge(AgentSkillRecoveryContext recovery)
    {
        var command = DescribeCommand(recovery);
        return
            "[System: This is an Ornn skill-backed slash command. " +
            $"Before any final answer, call `{OrnnSearchSkillsToolName}` with the command/task `{command}` and `scope` = `mixed`, " +
            $"then call `{UseSkillToolName}` for the best matching skill and continue the command. " +
            "Do not explain this policy to the user.]";
    }

    private static string BuildPrimarySkillNudge(AgentSkillRecoveryContext recovery)
    {
        var command = DescribeCommand(recovery);
        var skill = recovery.PrimarySkillName?.Trim();
        return
            "[System: This slash command must run through the Ornn skill " +
            $"`{skill}` before any final answer. Call `{UseSkillToolName}` with `skill` = `{skill}` " +
            $"and continue `{command}` from the loaded skill instructions. " +
            "Do not answer from the prompt alone and do not explain this policy to the user.]";
    }

    private static string BuildUseDiscoveredSkillNudge(AgentSkillRecoveryContext recovery, string searchResult)
    {
        var command = DescribeCommand(recovery);
        var result = TrimForPrompt(searchResult);
        return
            "[System: The Ornn skill search returned matching skills, but no skill has been loaded after that search. " +
            $"Before any final answer, call `{UseSkillToolName}` for the best matching skill from this result: `{result}`. " +
            $"Then continue `{command}` from the loaded skill instructions. " +
            "Only give a concise actionable failure if the matching skill cannot be loaded.]";
    }

    private static string BuildBlockerNudge(
        AgentSkillRecoveryContext recovery,
        IReadOnlyList<ChatMessage> messages,
        string? finalContent)
    {
        var blocker = SummarizeBlocker(messages, finalContent);
        var command = DescribeCommand(recovery);
        return
            "[System: You are following a loaded skill for an Ornn skill-backed slash command, " +
            "but you hit a blocker before completing it. " +
            $"Before any final answer, call `{OrnnSearchSkillsToolName}` with a concrete query for this blocker: `{blocker}`. " +
            $"Use `scope` = `mixed`, then call `{UseSkillToolName}` for the best matching skill and continue `{command}`. " +
            "Only give a concise actionable failure if Ornn lookup/load has already been tried for this blocker and cannot recover it.]";
    }

    private static string ExtractCommandArguments(AgentSkillRecoveryContext recovery)
    {
        var original = recovery.OriginalCommand?.Trim();
        var command = recovery.CommandName?.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(command))
            return string.Empty;

        var slashCommand = "/" + command;
        if (!original.StartsWith(slashCommand, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (original.Length == slashCommand.Length)
            return string.Empty;

        return char.IsWhiteSpace(original[slashCommand.Length])
            ? original[(slashCommand.Length + 1)..].Trim()
            : string.Empty;
    }

    private static string? ExtractFirstSkillName(string searchResult)
    {
        foreach (var rawLine in searchResult.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("- ", StringComparison.Ordinal))
                continue;

            var item = line[2..].Trim();
            if (item.StartsWith("**", StringComparison.Ordinal))
            {
                var end = item.IndexOf("**", 2, StringComparison.Ordinal);
                if (end > 2)
                    return item[2..end].Trim();
            }

            var paren = item.IndexOf(" (", StringComparison.Ordinal);
            var colon = item.IndexOf(':', StringComparison.Ordinal);
            var endIndex = item.Length;
            if (paren > 0)
                endIndex = Math.Min(endIndex, paren);
            if (colon > 0)
                endIndex = Math.Min(endIndex, colon);

            var candidate = item[..endIndex].Trim().Trim('*', '`');
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return null;
    }

    private static string SummarizeBlocker(IReadOnlyList<ChatMessage> messages, string? finalContent)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                ContainsAny(message.Content, BlockerPhrases))
            {
                return TrimForPrompt(message.Content);
            }
        }

        return TrimForPrompt(finalContent);
    }

    private static string DescribeCommand(AgentSkillRecoveryContext recovery)
    {
        if (!string.IsNullOrWhiteSpace(recovery.OriginalCommand))
            return recovery.OriginalCommand.Trim();

        if (!string.IsNullOrWhiteSpace(recovery.CommandName))
            return "/" + recovery.CommandName.Trim().TrimStart('/');

        if (!string.IsNullOrWhiteSpace(recovery.PrimarySkillName))
            return recovery.PrimarySkillName.Trim();

        return "the slash command";
    }

    private static string BuildInitialSearchQuery(AgentSkillRecoveryContext recovery)
    {
        if (!string.IsNullOrWhiteSpace(recovery.PrimarySkillName))
            return recovery.PrimarySkillName.Trim();

        if (!string.IsNullOrWhiteSpace(recovery.CommandName))
            return recovery.CommandName.Trim().TrimStart('/');

        return DescribeCommand(recovery);
    }

    private static bool IsTool(ToolCall call, string toolName) =>
        string.Equals(call.Name, toolName, StringComparison.OrdinalIgnoreCase);

    private static string TrimForPrompt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "the command could not be completed from the current skill/tool result";

        var compact = new StringBuilder(value.Length);
        var previousWhitespace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace)
                    compact.Append(' ');
                previousWhitespace = true;
                continue;
            }

            compact.Append(ch);
            previousWhitespace = false;
            if (compact.Length >= 360)
                break;
        }

        return compact.Length >= 360
            ? compact.ToString() + "..."
            : compact.ToString();
    }
}
