using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Chat;

internal static class SkillRecoveryPlanner
{
    internal const int MaxCallIdLength = 64;
    private const int CallIdFingerprintLength = 16;
    private const string CompactCallIdPrefix = "sr";
    private const string OrnnSearchSkillsToolName = "ornn_search_skills";
    private const string UseSkillToolName = "use_skill";

    private enum SkillDiscoveryBlockerDisposition
    {
        None = 0,
        Recoverable = 1,
        ConfigurationRequired = 2,
    }

    public readonly record struct RecoveryDirective(
        ToolCall? ToolCall,
        bool ConsumesOrnnSearchAttempt,
        string? Nudge,
        bool AttemptsPrimarySkill = false);

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
        // HTTP-style upstream failures surfaced through nyxid_proxy / Ornn / chrono-storage
        // tool envelopes — treat them as concrete blockers so the planner can fire a fresh
        // ornn_search_skills round instead of letting the LLM keep guessing repo/storage
        // paths after every 4xx/5xx.
        "\"status\":404",
        "\"status\":401",
        "\"status\":403",
        "\"status\":500",
        "\"status\":502",
        "\"status\":503",
        " 404",
        " 401",
        " 403",
        " 500",
        "bad request",
        "forbidden",
        "unauthorized",
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

    public static bool TryPlanNextDirective(
        AgentSkillRecoveryContext recovery,
        IReadOnlyList<ChatMessage> messages,
        string? finalContent,
        int recoveryAttempts,
        string? callIdPrefix,
        out RecoveryDirective directive) =>
        TryPlanNextDirective(
            recovery,
            messages,
            finalContent,
            recoveryAttempts,
            callIdPrefix,
            primarySkillAttempted: false,
            out directive);

    public static bool TryPlanNextDirective(
        AgentSkillRecoveryContext recovery,
        IReadOnlyList<ChatMessage> messages,
        string? finalContent,
        int recoveryAttempts,
        string? callIdPrefix,
        bool primarySkillAttempted,
        out RecoveryDirective directive)
    {
        directive = default;
        if (!IsEnabled(recovery))
            return false;

        var maxAttempts = recovery.MaxOrnnSearchAttempts > 0
            ? recovery.MaxOrnnSearchAttempts
            : 1;

        if (!primarySkillAttempted &&
            !string.IsNullOrWhiteSpace(recovery.PrimarySkillName) &&
            !HasUseSkillFor(messages, recovery.PrimarySkillName))
        {
            directive = new RecoveryDirective(
                BuildUseSkillToolCall(
                    BuildCallId(callIdPrefix, UseSkillToolName),
                    recovery.PrimarySkillName,
                    ExtractCommandArguments(recovery)),
                ConsumesOrnnSearchAttempt: false,
                Nudge: null,
                AttemptsPrimarySkill: true);
            return true;
        }

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

        if (TryGetLatestOrnnSearchWithMatches(messages, out var latestSearchIndex, out var latestSearchResult) &&
            !HasToolCallAfter(messages, UseSkillToolName, latestSearchIndex))
        {
            var skillName = latestSearchResult.Matches.Count > 0
                ? latestSearchResult.Matches[0].SkillName
                : null;
            directive = !string.IsNullOrWhiteSpace(skillName)
                ? new RecoveryDirective(
                    BuildUseSkillToolCall(
                        BuildCallId(callIdPrefix, UseSkillToolName),
                        skillName,
                        ExtractCommandArguments(recovery)),
                    ConsumesOrnnSearchAttempt: false,
                    Nudge: null)
                : recoveryAttempts >= maxAttempts
                    ? default
                    : new RecoveryDirective(
                    ToolCall: null,
                    ConsumesOrnnSearchAttempt: true,
                    Nudge: BuildUseDiscoveredSkillNudge(recovery, latestSearchResult.DisplayText));
            return directive.ToolCall is not null || !string.IsNullOrWhiteSpace(directive.Nudge);
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
                mount_workflows = true,
            }),
        };

    private static string BuildCallId(string? callIdPrefix, string toolName)
    {
        var suffix = toolName.Replace('_', '-');
        var callId = string.IsNullOrWhiteSpace(callIdPrefix)
            ? $"skill-recovery:{suffix}"
            : $"{callIdPrefix}:skill-recovery:{suffix}";

        return EnsureCallIdLimit(callId);
    }

    internal static string EnsureCallIdLimit(string callId)
    {
        if (callId.Length <= MaxCallIdLength)
            return callId;

        var readableSegment = ExtractReadableCallIdSegment(callId);
        var fingerprint = BuildCallIdFingerprint(callId);
        var readableBudget = MaxCallIdLength
            - CompactCallIdPrefix.Length
            - 2
            - fingerprint.Length;
        if (readableSegment.Length > readableBudget)
            readableSegment = readableSegment[^readableBudget..];

        return $"{CompactCallIdPrefix}:{readableSegment}:{fingerprint}";
    }

    internal static string BuildSequencedCallId(string? callId, int sequence)
    {
        if (string.IsNullOrWhiteSpace(callId))
            return EnsureCallIdLimit($"skill-recovery:{sequence}");

        var sequenceSegment = $"recovery:{sequence}";
        return EnsureCallIdLimit(AppendCallIdSegment(callId, sequenceSegment));
    }

    private static string AppendCallIdSegment(string callId, string segment)
    {
        if (TrySplitCompactCallId(callId, out var readableSegment, out var fingerprint))
            return $"{CompactCallIdPrefix}:{readableSegment}:{segment}:{fingerprint}";

        return $"{callId}:{segment}";
    }

    private static string ExtractReadableCallIdSegment(string callId)
    {
        if (TrySplitCompactCallId(callId, out var compactReadableSegment, out _))
            return compactReadableSegment;

        var markerIndex = callId.IndexOf(":skill-recovery:", StringComparison.Ordinal);
        if (markerIndex >= 0)
            return callId[(markerIndex + ":skill-recovery:".Length)..];

        var recoveryIndex = callId.IndexOf("skill-recovery:", StringComparison.Ordinal);
        if (recoveryIndex >= 0)
            return callId[(recoveryIndex + "skill-recovery:".Length)..];

        var lastSeparator = callId.LastIndexOf(':');
        return lastSeparator >= 0 && lastSeparator < callId.Length - 1
            ? callId[(lastSeparator + 1)..]
            : callId;
    }

    private static bool TrySplitCompactCallId(
        string callId,
        out string readableSegment,
        out string fingerprint)
    {
        readableSegment = string.Empty;
        fingerprint = string.Empty;

        if (!callId.StartsWith($"{CompactCallIdPrefix}:", StringComparison.Ordinal))
            return false;

        var segments = callId[(CompactCallIdPrefix.Length + 1)..].Split(':');
        if (segments.Length < 2 || !IsCallIdFingerprint(segments[^1]))
            return false;

        fingerprint = segments[^1];
        readableSegment = string.Join(':', segments[..^1]);
        return !string.IsNullOrWhiteSpace(readableSegment);
    }

    private static bool IsCallIdFingerprint(string value) =>
        value.Length == CallIdFingerprintLength &&
        value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string BuildCallIdFingerprint(string callId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(callId));
        return Convert.ToHexString(hash)[..CallIdFingerprintLength].ToLowerInvariant();
    }

    private static bool IsEnabled(AgentSkillRecoveryContext recovery) =>
        recovery.RequireInitialOrnnSearch || recovery.RequireOrnnSearchOnBlocker || recovery.DiscoveryRequested;

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
        out SkillSearchToolResultView searchResult)
    {
        searchMessageIndex = -1;
        searchResult = new SkillSearchToolResultView(ToolResultViewStatus.Unknown, false, [], null, null, string.Empty);

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
                var toolResult = FindToolResult(messages, call.Id, i + 1);
                searchResult = toolResult?.ToolResultView?.SkillSearch
                               ?? new SkillSearchToolResultView(ToolResultViewStatus.Unknown, false, [], null, null, string.Empty);
                return searchResult.Status == ToolResultViewStatus.Success &&
                       searchResult.HasMatches;
            }
        }

        return false;
    }

    private static ChatMessage? FindToolResult(IReadOnlyList<ChatMessage> messages, string? callId, int startIndex)
    {
        if (string.IsNullOrWhiteSpace(callId))
            return null;

        for (var i = Math.Max(0, startIndex); i < messages.Count; i++)
        {
            var message = messages[i];
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(message.ToolCallId, callId, StringComparison.Ordinal))
            {
                return message;
            }
        }

        return null;
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

        for (var i = messages.Count - 1; i >= start; i--)
        {
            var message = messages[i];
            if (!string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
                continue;

            switch (ClassifyToolBlocker(message))
            {
                case SkillDiscoveryBlockerDisposition.ConfigurationRequired:
                    return false;
                case SkillDiscoveryBlockerDisposition.Recoverable:
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

    private static SkillDiscoveryBlockerDisposition ClassifyToolBlocker(ChatMessage message)
    {
        if (string.Equals(
                message.ToolResultView?.Failure?.ErrorCode,
                AgentToolFailureCodes.ChannelWorkflowResultDeliveryUnavailable,
                StringComparison.Ordinal))
        {
            return SkillDiscoveryBlockerDisposition.ConfigurationRequired;
        }

        var view = message.ToolResultView;
        if (view?.SkillSearch is { } searchResult)
        {
            return searchResult.Status == ToolResultViewStatus.Error
                ? SkillDiscoveryBlockerDisposition.Recoverable
                : SkillDiscoveryBlockerDisposition.None;
        }

        if (view?.SkillLoad is { } loadResult)
        {
            return loadResult.Status is ToolResultViewStatus.Error or ToolResultViewStatus.NotFound
                ? SkillDiscoveryBlockerDisposition.Recoverable
                : SkillDiscoveryBlockerDisposition.None;
        }

        return ContainsAny(message.Content, BlockerPhrases)
            ? SkillDiscoveryBlockerDisposition.Recoverable
            : SkillDiscoveryBlockerDisposition.None;
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

    private static string ExtractCommandArguments(AgentSkillRecoveryContext recovery)
    {
        if (!string.IsNullOrWhiteSpace(recovery.CommandArguments))
            return recovery.CommandArguments.Trim();

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

    private static string SummarizeBlocker(IReadOnlyList<ChatMessage> messages, string? finalContent)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                ClassifyToolBlocker(message) == SkillDiscoveryBlockerDisposition.Recoverable)
            {
                if (message.ToolResultView?.SkillSearch is { Error: { } searchError })
                    return TrimForPrompt(searchError);

                if (message.ToolResultView?.SkillLoad is { Error: { } loadError })
                    return TrimForPrompt(loadError);

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

        return recovery.DiscoveryRequested ? "skill discovery" : "the skill command";
    }

    private static string BuildInitialSearchQuery(AgentSkillRecoveryContext recovery)
    {
        if (!string.IsNullOrWhiteSpace(recovery.PrimarySkillName))
            return recovery.PrimarySkillName.Trim();

        if (!string.IsNullOrWhiteSpace(recovery.CommandName))
            return recovery.CommandName.Trim().TrimStart('/');

        if (recovery.DiscoveryRequested)
            return "skill discovery";

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
