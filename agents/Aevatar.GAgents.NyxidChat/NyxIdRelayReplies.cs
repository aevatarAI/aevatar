using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.Configuration;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdRelayReplies
{
    // Refactor (iter17/cluster-038):
    //   Old pattern: Nyx relay replay/idempotency 和 reply 累积在 process-local ConcurrentDictionary/lock(NyxRelayBridgeIdempotencyGuard / NyxIdRelayReplayGuard / NyxIdRelayReplyAccumulator)。
    //   New principle: ConversationGAgent persist callback_jti admission 为 typed event 优先于 business work;删除 process-local replay guards + dead accumulator。
    public static string ClassifyError(string error)
    {
        if (error.Contains("403", StringComparison.Ordinal) ||
            error.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            return "Sorry, I can't reach the AI service right now (403 Forbidden).";

        if (error.Contains("401", StringComparison.Ordinal) ||
            error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return "Sorry, authentication with the AI service failed (401).";

        if (error.Contains("429", StringComparison.Ordinal) ||
            error.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("too many", StringComparison.OrdinalIgnoreCase))
            return "Sorry, the AI service is busy right now (429). Please wait a moment and try again.";

        if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "Sorry, the AI service took too long to respond. Please try again.";

        if (error.Contains("model", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "Sorry, the configured AI model is not available.";

        return "Sorry, something went wrong while generating a response.";
    }

    public static bool TryExtractLlmError(string? content, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        const string llmErrorPrefix = "[[AEVATAR_LLM_ERROR]]";
        const string llmFailedPrefix = "LLM request failed:";
        if (content.StartsWith(llmErrorPrefix, StringComparison.Ordinal))
        {
            error = content[llmErrorPrefix.Length..].Trim();
            return true;
        }

        if (content.StartsWith(llmFailedPrefix, StringComparison.Ordinal))
        {
            error = content.Trim();
            return true;
        }

        return false;
    }

    public static string BuildDiagnostic(
        Google.Protobuf.Collections.MapField<string, string> metadata,
        IConfiguration? configuration,
        string errorMessage)
    {
        var modelOverride = metadata.TryGetValue(LLMRequestMetadataKeys.ModelOverride, out var m) ? m : null;
        var serverDefault = configuration?["Aevatar:NyxId:DefaultModel"] ?? "(OpenAIModel option)";
        var route = metadata.TryGetValue(LLMRequestMetadataKeys.NyxIdRoutePreference, out var r)
            && !string.IsNullOrWhiteSpace(r) ? r : "gateway";
        var hasToken = metadata.ContainsKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        var scope = metadata.TryGetValue("scope_id", out var s) ? s : "<unknown>";

        var model = !string.IsNullOrWhiteSpace(modelOverride)
            ? $"{modelOverride} (from user config)"
            : $"server-default={serverDefault}";

        var error = errorMessage.Length > 300 ? errorMessage[..300] + "..." : errorMessage;

        return $"Model: {model}\nRoute: {route}\nScope: {scope}\nToken: {(hasToken ? "present" : "MISSING")}\nError: {error}";
    }
}
