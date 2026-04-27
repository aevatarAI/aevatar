using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Shared parsing / rollback helpers for the Nyx-side responses consumed by per-platform
/// provisioning services (<see cref="NyxLarkProvisioningService"/>, <see cref="NyxTelegramProvisioningService"/>,
/// future platforms). Centralized here so the Lark and Telegram services do not drift on the
/// JSON shape Nyx returns and the failure-string contract surfaced through the registration
/// endpoint stays uniform.
/// </summary>
internal static class NyxApiResponseHelper
{
    /// <summary>
    /// Returns the trimmed <c>id</c> field from a Nyx create-resource response, or throws
    /// <see cref="InvalidOperationException"/> with a controlled error code suffix derived from
    /// <paramref name="resourceName"/>. Wraps <see cref="LooksLikeErrorEnvelope"/> + <see cref="ExtractErrorDetail"/>.
    /// </summary>
    public static string ExtractRequiredId(string response, string resourceName)
    {
        if (LooksLikeErrorEnvelope(response))
            throw new InvalidOperationException($"{resourceName}_request_failed {ExtractErrorDetail(response)}");

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"missing_id_in_{resourceName}_response");

            var id = idElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException($"empty_id_in_{resourceName}_response");

            return id;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"invalid_json_in_{resourceName}_response", ex);
        }
    }

    /// <summary>
    /// Returns the trimmed <c>id</c> field from a Nyx api-key creation response. Distinct from
    /// <see cref="ExtractRequiredId"/> because the legacy error code surface uses the
    /// <c>api_key_id_request_failed</c> prefix specifically.
    /// </summary>
    public static string ExtractRequiredApiKeyId(string response)
    {
        if (LooksLikeErrorEnvelope(response))
            throw new InvalidOperationException($"api_key_id_request_failed {ExtractErrorDetail(response)}");

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("missing_id_in_api_key_id_response");

            var id = idElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("empty_id_in_api_key_id_response");

            return id;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"invalid_json_in_api_key_id_response {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Returns true when the response either is unparseable or carries a top-level <c>"error":true</c>
    /// envelope (the wrapping shape Nyx applies to non-2xx HTTP responses from the upstream platform).
    /// </summary>
    public static bool LooksLikeErrorEnvelope(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return true;

        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.TryGetProperty("error", out var errorProp) &&
                   errorProp.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// Builds a single-line diagnostic string from a Nyx error envelope, surfacing the
    /// <c>status</c>, <c>body</c>, and <c>message</c> fields when present.
    /// </summary>
    public static string ExtractErrorDetail(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "empty_response";

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.Number
                ? statusElement.GetInt32().ToString()
                : "unknown";
            var body = root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString()
                : string.Empty;
            var message = root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : string.Empty;

            return $"nyx_status={status}" +
                   (string.IsNullOrWhiteSpace(body) ? string.Empty : $" body={body}") +
                   (string.IsNullOrWhiteSpace(message) ? string.Empty : $" message={message}");
        }
        catch (JsonException)
        {
            return "invalid_error_envelope";
        }
    }

    /// <summary>
    /// Best-effort delete of a Nyx resource during provisioning rollback. Logs both the
    /// error-envelope and exception cases and never re-throws, so a failed rollback never
    /// shadows the original provisioning failure that triggered it.
    /// </summary>
    public static async Task TryRollbackAsync(
        Func<Task<string>> rollback,
        string resourceType,
        string resourceId,
        ILogger logger)
    {
        try
        {
            var response = await rollback();
            if (LooksLikeErrorEnvelope(response))
            {
                logger.LogWarning(
                    "Nyx rollback returned an error envelope: type={ResourceType}, id={ResourceId}, response={Response}",
                    resourceType,
                    resourceId,
                    response);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Nyx rollback failed: type={ResourceType}, id={ResourceId}",
                resourceType,
                resourceId);
        }
    }

    /// <summary>
    /// Returns a client-safe failure reason. <see cref="InvalidOperationException"/> instances
    /// thrown by the helpers in this class carry controlled, structured error codes (e.g.
    /// <c>channel_bot_id_request_failed nyx_status=401 body=invalid app secret</c>) so they are
    /// safe to surface verbatim. Anything else (HTTP transport errors, generic exceptions)
    /// collapses to <c>provisioning_failed</c> so endpoint paths, internal state, and stack
    /// fragments do not leak through the registration response. Callers should still log the
    /// full exception out-of-band for operational triage.
    /// </summary>
    public static string SanitizeFailureReason(Exception ex) =>
        ex is InvalidOperationException ? ex.Message : "provisioning_failed";
}
