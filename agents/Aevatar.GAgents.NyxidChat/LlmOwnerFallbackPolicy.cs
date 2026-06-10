using System.Net.Http;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.GAgents.NyxidChat;

internal static class LlmOwnerFallbackPolicy
{
    public const string OwnerFallbackRequestedErrorCode = "llm_reply_owner_fallback_requested";
    public const string DefaultFailureErrorCode = "llm_reply_failed";

    public static bool IsRetryable(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex is TaskCanceledException taskCanceled && !taskCanceled.CancellationToken.IsCancellationRequested)
            return true;

        if (ex is OperationCanceledException)
            return false;

        if (ex is HttpRequestException
            or TimeoutException
            or JsonException
            or System.IO.IOException
            or NyxIdAuthenticationRequiredException)
        {
            return true;
        }

        if (ex is NyxIdUpstreamException upstream)
            return IsRetryableUpstream(upstream);

        return ex is InvalidOperationException invalid &&
               (IsKnownNyxIdRouteFailure(invalid.Message) || LooksLikeLlmRequestRejection(invalid.Message));
    }

    public static bool IsOwnerFallbackRequested(string? errorCode) =>
        string.Equals(errorCode, OwnerFallbackRequestedErrorCode, StringComparison.Ordinal);

    private static bool IsRetryableUpstream(NyxIdUpstreamException upstream) =>
        upstream.Kind is NyxIdUpstreamFailureKind.ServiceUnavailable
                or NyxIdUpstreamFailureKind.RateLimited
                or NyxIdUpstreamFailureKind.AuthenticationFailed
                or NyxIdUpstreamFailureKind.RequestRejected
                or NyxIdUpstreamFailureKind.UpstreamServerError
                or NyxIdUpstreamFailureKind.ProviderError
            || upstream.Status is 400 or 401 or 403 or 404 or 408 or 409 or 429 or >= 500;

    private static bool IsKnownNyxIdRouteFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lowered = message.ToLowerInvariant();
        return lowered.Contains("nyxid", StringComparison.Ordinal)
               || lowered.Contains("binding", StringComparison.Ordinal)
               || lowered.Contains("scope", StringComparison.Ordinal)
               || lowered.Contains("token", StringComparison.Ordinal)
               || lowered.Contains("401", StringComparison.Ordinal)
               || lowered.Contains("403", StringComparison.Ordinal)
               || lowered.Contains("not found", StringComparison.Ordinal)
               || lowered.Contains("revoked", StringComparison.Ordinal)
               || lowered.Contains("route", StringComparison.Ordinal)
               || lowered.Contains("proxy", StringComparison.Ordinal);
    }

    private static bool LooksLikeLlmRequestRejection(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lowered = message.ToLowerInvariant();
        return lowered.Contains("invalid schema", StringComparison.Ordinal)
               || lowered.Contains("schema", StringComparison.Ordinal)
               || lowered.Contains("function", StringComparison.Ordinal)
               || lowered.Contains("tool", StringComparison.Ordinal)
               || lowered.Contains("oneof", StringComparison.Ordinal)
               || lowered.Contains("bad request", StringComparison.Ordinal)
               || lowered.Contains("request rejected", StringComparison.Ordinal)
               || lowered.Contains("http 400", StringComparison.Ordinal)
               || lowered.Contains("400", StringComparison.Ordinal);
    }
}
