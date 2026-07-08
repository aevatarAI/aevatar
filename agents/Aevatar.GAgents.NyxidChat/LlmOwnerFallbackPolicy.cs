using System.Net.Http;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.GAgents.NyxidChat;

internal static class LlmOwnerFallbackPolicy
{
    public const string OwnerFallbackRequestedErrorCode = "llm_reply_owner_fallback_requested";
    public const string DefaultFailureErrorCode = "llm_reply_failed";

    // Owner fallback is a degraded no-tools retry, so eligibility must come from typed
    // exception facts only. LLM providers classify upstream failures into
    // NyxIdUpstreamException / NyxIdAuthenticationRequiredException before they escape
    // (NyxIdLLMProvider.EnrichErrors), so a plain InvalidOperationException reaching this
    // policy is plumbing, configuration, or tool-discovery breakage — failing on the owner
    // config too — and must surface as a step failure instead of silently converting the
    // turn into a no-tools owner step (2026-07 incident, funnel B).
    public static bool IsRetryable(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex is TaskCanceledException taskCanceled && !taskCanceled.CancellationToken.IsCancellationRequested)
            return true;

        if (ex is OperationCanceledException)
            return false;

        if (ex is NyxIdUpstreamException upstream)
            return IsRetryableUpstream(upstream);

        return ex is HttpRequestException
            or TimeoutException
            or JsonException
            or System.IO.IOException
            or NyxIdAuthenticationRequiredException;
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
}
