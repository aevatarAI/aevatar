using System.Text.Json;

namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// Helper methods for tolerant primitive parameter parsing.
/// Accepts common LLM-generated variants such as JSON arrays and escaped delimiters.
/// </summary>
public static class WorkflowParameterValueParser
{
    public static int ResolveTimeoutSeconds(
        IReadOnlyDictionary<string, string> parameters,
        int defaultSeconds,
        int minSeconds = 1,
        int maxSeconds = 86_400)
    {
        var timeoutSeconds = GetBoundedInt(
            parameters,
            defaultSeconds,
            minSeconds,
            maxSeconds,
            "timeout",
            "timeout_seconds");
        if (parameters.ContainsKey("timeout") || parameters.ContainsKey("timeout_seconds"))
            return timeoutSeconds;

        var timeoutMs = GetBoundedInt(
            parameters,
            0,
            0,
            maxSeconds * 1000,
            "timeout_ms");
        if (timeoutMs <= 0)
            return timeoutSeconds;

        return Math.Clamp((int)Math.Ceiling(timeoutMs / 1000d), minSeconds, maxSeconds);
    }

    public static string GetString(
        IReadOnlyDictionary<string, string> parameters,
        string fallback,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return fallback;
    }

    public static int GetBoundedInt(
        IReadOnlyDictionary<string, string> parameters,
        int fallback,
        int min,
        int max,
        params string[] keys)
    {
        if (!TryGetBoundedInt(parameters, out var value, min, max, keys))
            return fallback;

        return value;
    }

    public static bool TryGetBoundedInt(
        IReadOnlyDictionary<string, string> parameters,
        out int value,
        int min,
        int max,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!parameters.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                continue;

            if (!int.TryParse(raw.Trim(), out var parsed))
                continue;

            value = Math.Clamp(parsed, min, max);
            return true;
        }

        value = default;
        return false;
    }

    public static List<string> GetStringList(
        IReadOnlyDictionary<string, string> parameters,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!parameters.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                continue;

            return ParseStringList(raw);
        }

        return [];
    }

    public static bool GetBool(
        IReadOnlyDictionary<string, string> parameters,
        bool fallback,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!parameters.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                continue;

            if (TryParseBool(raw, out var value))
                return value;
        }

        return fallback;
    }

    public static List<string> ParseStringList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var trimmed = raw.Trim();
        if (TryParseJsonArray(trimmed, out var jsonValues))
            return jsonValues;

        return trimmed
            .Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeToken)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public static string NormalizeEscapedText(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        var normalized = UnwrapQuotes(raw.Trim());
        normalized = normalized
            .Replace("\\r\\n", "\r\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal);
        return string.IsNullOrEmpty(normalized) ? fallback : normalized;
    }

    public static string[] SplitInputByDelimiterOrJsonArray(string? input, string delimiter)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        var trimmed = input.Trim();
        if (TryParseJsonArray(trimmed, out var jsonValues) && jsonValues.Count > 0)
            return jsonValues.ToArray();

        var actualDelimiter = string.IsNullOrEmpty(delimiter) ? "\n---\n" : delimiter;
        return input.Split(actualDelimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool TryParseJsonArray(string raw, out List<string> values)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                values = [];
                return false;
            }

            values = [];
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var text = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => null,
                    _ => element.GetRawText(),
                };
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                values.Add(SanitizeToken(text));
            }

            return true;
        }
        catch (JsonException)
        {
            values = [];
            return false;
        }
    }

    private static string SanitizeToken(string token) =>
        UnwrapQuotes(token
            .Trim()
            .TrimStart('[')
            .TrimEnd(']'))
            .Trim();

    private static string UnwrapQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];

        return value;
    }

    private static bool TryParseBool(string raw, out bool value)
    {
        var normalized = raw.Trim();
        if (bool.TryParse(normalized, out value))
            return true;

        if (string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "n", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }
}
