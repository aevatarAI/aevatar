using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.AI.Core.Chat;

internal static class SkillRecoveryToolResultViews
{
    private const string OrnnSearchSkillsToolName = "ornn_search_skills";
    private const string UseSkillToolName = "use_skill";

    public static ChatMessage Attach(
        ChatMessage message,
        string? toolName,
        string rawToolResult,
        AgentToolReceipt? receipt = null)
    {
        var parsedView = Parse(toolName, rawToolResult);
        var failureView = CreateFailureView(receipt);
        if (parsedView is null && failureView is null)
            return message;

        var view = parsedView is null
            ? new ToolResultView(
                NormalizeToolName(toolName, receipt),
                SkillSearch: null,
                SkillLoad: null,
                Failure: failureView)
            : parsedView with { Failure = failureView };
        var displayText = view.SkillSearch?.DisplayText ?? view.SkillLoad?.DisplayText ?? message.Content;
        return new ChatMessage
        {
            Role = message.Role,
            Content = displayText,
            ReasoningContent = message.ReasoningContent,
            ContentParts = message.ContentParts,
            ToolCallId = message.ToolCallId,
            ToolCalls = message.ToolCalls,
            ToolResultView = view,
        };
    }

    private static ToolFailureResultView? CreateFailureView(AgentToolReceipt? receipt)
    {
        if (receipt?.Status is not (
                AgentToolReceiptStatus.Error or
                AgentToolReceiptStatus.Denied or
                AgentToolReceiptStatus.AuthorizationRequired))
        {
            return null;
        }

        var safeMessage = receipt.Status == AgentToolReceiptStatus.AuthorizationRequired
            ? receipt.AuthorizationRequired?.SafeMessage
            : null;
        if (string.IsNullOrWhiteSpace(safeMessage))
            safeMessage = receipt.ErrorMessage;

        return new ToolFailureResultView(
            receipt.Status,
            receipt.ErrorCode?.Trim() ?? string.Empty,
            safeMessage?.Trim() ?? string.Empty);
    }

    private static string NormalizeToolName(
        string? toolName,
        AgentToolReceipt? receipt)
    {
        var normalized = toolName?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        return receipt?.ToolName?.Trim() ?? string.Empty;
    }

    public static ToolResultView? Parse(string? toolName, string? rawToolResult)
    {
        if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(rawToolResult))
            return null;

        if (string.Equals(toolName, OrnnSearchSkillsToolName, StringComparison.OrdinalIgnoreCase))
            return new ToolResultView(toolName, ParseSearchResult(rawToolResult), null);

        if (string.Equals(toolName, UseSkillToolName, StringComparison.OrdinalIgnoreCase))
            return new ToolResultView(toolName, null, ParseLoadResult(rawToolResult));

        return null;
    }

    private static SkillSearchToolResultView ParseSearchResult(string rawToolResult)
    {
        if (TryParseJsonObject(rawToolResult, out var root) &&
            root.TryGetProperty("result_type", out var resultType) &&
            string.Equals(resultType.GetString(), "skill_search", StringComparison.OrdinalIgnoreCase))
        {
            return ParseStructuredSearchResult(root, rawToolResult);
        }

        return ParseLegacySearchResult(rawToolResult);
    }

    private static SkillLoadToolResultView ParseLoadResult(string rawToolResult)
    {
        if (TryParseJsonObject(rawToolResult, out var root) &&
            root.TryGetProperty("result_type", out var resultType) &&
            string.Equals(resultType.GetString(), "skill_load", StringComparison.OrdinalIgnoreCase))
        {
            return ParseStructuredLoadResult(root, rawToolResult);
        }

        return ParseLegacyLoadResult(rawToolResult);
    }

    private static SkillSearchToolResultView ParseStructuredSearchResult(JsonElement root, string fallbackDisplayText)
    {
        var matches = new List<SkillSearchMatchView>();
        if (root.TryGetProperty("matches", out var matchesElement) && matchesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var match in matchesElement.EnumerateArray())
            {
                if (match.ValueKind != JsonValueKind.Object)
                    continue;

                var skillName = TryGetString(match, "skill_name") ?? TryGetString(match, "name");
                if (string.IsNullOrWhiteSpace(skillName))
                    continue;

                matches.Add(new SkillSearchMatchView(
                    SkillName: skillName,
                    Description: TryGetString(match, "description"),
                    IsPrivate: TryGetBoolean(match, "is_private"),
                    Category: TryGetString(match, "category"),
                    Tags: ReadStringArray(match, "tags")));
            }
        }

        return new SkillSearchToolResultView(
            Status: ParseStatus(root, hasMatchesFallback: matches.Count > 0),
            HasMatches: matches.Count > 0,
            Matches: matches,
            Error: TryGetString(root, "error"),
            HttpStatus: TryGetInt32(root, "http_status"),
            DisplayText: TryGetString(root, "text") ?? fallbackDisplayText);
    }

    private static SkillLoadToolResultView ParseStructuredLoadResult(JsonElement root, string fallbackDisplayText)
    {
        var loaded = TryGetBoolean(root, "loaded");
        return new SkillLoadToolResultView(
            Status: ParseStatus(root, hasMatchesFallback: loaded),
            SkillName: TryGetString(root, "skill_name"),
            Loaded: loaded,
            Error: TryGetString(root, "error"),
            HttpStatus: TryGetInt32(root, "http_status"),
            DisplayText: TryGetString(root, "text") ?? fallbackDisplayText);
    }

    private static SkillSearchToolResultView ParseLegacySearchResult(string rawToolResult)
    {
        if (TryExtractStatusFromLegacyJson(rawToolResult, out var errorStatus))
        {
            return new SkillSearchToolResultView(
                Status: ToolResultViewStatus.Error,
                HasMatches: false,
                Matches: [],
                Error: TrimForPrompt(rawToolResult),
                HttpStatus: errorStatus,
                DisplayText: rawToolResult);
        }

        if (rawToolResult.Contains("Search failed", StringComparison.OrdinalIgnoreCase) ||
            rawToolResult.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
            rawToolResult.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
            rawToolResult.Contains("No NyxID access token", StringComparison.OrdinalIgnoreCase))
        {
            return new SkillSearchToolResultView(
                Status: ToolResultViewStatus.Error,
                HasMatches: false,
                Matches: [],
                Error: TrimForPrompt(rawToolResult),
                HttpStatus: ExtractInlineHttpStatus(rawToolResult),
                DisplayText: rawToolResult);
        }

        if (rawToolResult.Contains("No skills found", StringComparison.OrdinalIgnoreCase))
        {
            return new SkillSearchToolResultView(
                Status: ToolResultViewStatus.NoMatch,
                HasMatches: false,
                Matches: [],
                Error: null,
                HttpStatus: null,
                DisplayText: rawToolResult);
        }

        var matches = ExtractLegacySearchMatches(rawToolResult);
        return new SkillSearchToolResultView(
            Status: rawToolResult.Contains("Found ", StringComparison.OrdinalIgnoreCase) &&
                    rawToolResult.Contains("skill", StringComparison.OrdinalIgnoreCase)
                ? ToolResultViewStatus.Success
                : matches.Count > 0
                    ? ToolResultViewStatus.Success
                    : ToolResultViewStatus.Unknown,
            HasMatches: rawToolResult.Contains("Found ", StringComparison.OrdinalIgnoreCase) &&
                        rawToolResult.Contains("skill", StringComparison.OrdinalIgnoreCase) ||
                        matches.Count > 0,
            Matches: matches,
            Error: null,
            HttpStatus: null,
            DisplayText: rawToolResult);
    }

    private static SkillLoadToolResultView ParseLegacyLoadResult(string rawToolResult)
    {
        if (TryParseJsonObject(rawToolResult, out var root))
        {
            var error = TryGetString(root, "error");
            if (!string.IsNullOrWhiteSpace(error))
            {
                return new SkillLoadToolResultView(
                    Status: ToolResultViewStatus.Error,
                    SkillName: null,
                    Loaded: false,
                    Error: error,
                    HttpStatus: TryGetInt32(root, "status"),
                    DisplayText: rawToolResult);
            }
        }

        if (rawToolResult.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return new SkillLoadToolResultView(
                Status: ToolResultViewStatus.NotFound,
                SkillName: null,
                Loaded: false,
                Error: TrimForPrompt(rawToolResult),
                HttpStatus: ExtractInlineHttpStatus(rawToolResult),
                DisplayText: rawToolResult);
        }

        return new SkillLoadToolResultView(
            Status: ToolResultViewStatus.Success,
            SkillName: ExtractLegacyLoadedSkillName(rawToolResult),
            Loaded: true,
            Error: null,
            HttpStatus: null,
            DisplayText: rawToolResult);
    }

    private static List<SkillSearchMatchView> ExtractLegacySearchMatches(string rawToolResult)
    {
        var matches = new List<SkillSearchMatchView>();
        foreach (var rawLine in rawToolResult.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("- ", StringComparison.Ordinal))
                continue;

            var item = line[2..].Trim();
            string? skillName = null;
            if (item.StartsWith("**", StringComparison.Ordinal))
            {
                var end = item.IndexOf("**", 2, StringComparison.Ordinal);
                if (end > 2)
                    skillName = item[2..end].Trim();
            }

            if (string.IsNullOrWhiteSpace(skillName))
            {
                var paren = item.IndexOf(" (", StringComparison.Ordinal);
                var colon = item.IndexOf(':', StringComparison.Ordinal);
                var endIndex = item.Length;
                if (paren > 0)
                    endIndex = Math.Min(endIndex, paren);
                if (colon > 0)
                    endIndex = Math.Min(endIndex, colon);

                skillName = item[..endIndex].Trim().Trim('*', '`');
            }

            if (string.IsNullOrWhiteSpace(skillName))
                continue;

            matches.Add(new SkillSearchMatchView(skillName, Description: null, IsPrivate: false, Category: null, Tags: []));
        }

        return matches;
    }

    private static string? ExtractLegacyLoadedSkillName(string rawToolResult)
    {
        if (!rawToolResult.StartsWith("# ", StringComparison.Ordinal))
            return null;

        var end = rawToolResult.IndexOf('\n');
        if (end <= 2)
            return null;

        var name = rawToolResult[2..end].Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static ToolResultViewStatus ParseStatus(JsonElement root, bool hasMatchesFallback)
    {
        var status = TryGetString(root, "status");
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                return ToolResultViewStatus.Success;
            if (string.Equals(status, "no_match", StringComparison.OrdinalIgnoreCase))
                return ToolResultViewStatus.NoMatch;
            if (string.Equals(status, "not_found", StringComparison.OrdinalIgnoreCase))
                return ToolResultViewStatus.NotFound;
            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                return ToolResultViewStatus.Error;
        }

        if (!string.IsNullOrWhiteSpace(TryGetString(root, "error")))
            return ToolResultViewStatus.Error;

        return hasMatchesFallback ? ToolResultViewStatus.Success : ToolResultViewStatus.Unknown;
    }

    private static bool TryParseJsonObject(string rawToolResult, out JsonElement root)
    {
        root = default;
        try
        {
            using var document = JsonDocument.Parse(rawToolResult);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? TryGetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? TryGetInt32(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;

    private static bool TryGetBoolean(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var value = item.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value);
        }

        return result;
    }

    private static bool TryExtractStatusFromLegacyJson(string rawToolResult, out int status)
    {
        status = 0;
        if (!TryParseJsonObject(rawToolResult, out var root) ||
            !root.TryGetProperty("status", out var property) ||
            property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return property.TryGetInt32(out status);
    }

    private static int? ExtractInlineHttpStatus(string rawToolResult)
    {
        var candidates = new[] { " 404", " 401", " 403", " 500", " 502", " 503" };
        foreach (var candidate in candidates)
        {
            if (rawToolResult.Contains(candidate, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(candidate.AsSpan(1), out var status))
            {
                return status;
            }
        }

        return null;
    }

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
