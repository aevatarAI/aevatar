using System.Globalization;

namespace Aevatar.Workflow.Core;

internal static class WorkflowCapabilityValueParsers
{
    public static bool IsTimeoutError(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        error.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase);

    public static int ResolveLlmTimeoutMs(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("llm_timeout_ms", out var llmTimeoutRaw) &&
            int.TryParse(llmTimeoutRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var llmTimeoutMs) &&
            llmTimeoutMs > 0)
        {
            return llmTimeoutMs;
        }

        if (parameters.TryGetValue("timeout_ms", out var timeoutRaw) &&
            int.TryParse(timeoutRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutMs) &&
            timeoutMs > 0)
        {
            return timeoutMs;
        }

        return 1_800_000;
    }

    public static bool TryExtractLlmFailure(string? content, out string error)
    {
        const string prefix = "[[AEVATAR_LLM_ERROR]]";
        if (string.IsNullOrEmpty(content) || !content.StartsWith(prefix, StringComparison.Ordinal))
        {
            error = string.Empty;
            return false;
        }

        var extracted = content[prefix.Length..].Trim();
        error = string.IsNullOrWhiteSpace(extracted) ? "LLM call failed." : extracted;
        return true;
    }

    public static double ParseScore(string text)
    {
        var trimmed = text.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            return numeric;

        foreach (var token in trimmed.Split([' ', '\n', '\r', ',', '/', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return 0;
    }

    public static string ShortenKey(string key) =>
        key.Length > 60 ? key[..60] + "..." : key;
}
