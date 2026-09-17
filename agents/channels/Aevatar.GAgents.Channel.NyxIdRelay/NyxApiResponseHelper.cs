using System.Net;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Helpers;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>NyxID response parsing, safe public errors and owned-resource compensation.</summary>
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
            var root = document.RootElement;
            // A list response (e.g. GET /api/v1/channel-bots) is a JSON array, never an error
            // envelope; guard the object check so TryGetProperty does not throw on a non-object root.
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("error", out var errorProp) &&
                   errorProp.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// Whether a wrapped non-2xx response is NyxID's typed rejection for using a
    /// <c>scheduled_invocation</c> key outside its durable proxy-only route. Such a key cannot
    /// inspect its own API-key record, so readiness must recognize this error before <c>purpose</c>
    /// can be read and direct the owner to rebind the registration.
    /// </summary>
    public static bool IsDurableGrantMismatchError(string response)
    {
        try
        {
            using var envelope = JsonDocument.Parse(response);
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var envelopeError) ||
                envelopeError.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.Number ||
                !status.TryGetInt32(out var httpStatus) ||
                httpStatus != (int)HttpStatusCode.Forbidden ||
                !root.TryGetProperty("body", out var body) ||
                body.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            using var providerError = JsonDocument.Parse(body.GetString() ?? string.Empty);
            var providerRoot = providerError.RootElement;
            return providerRoot.ValueKind == JsonValueKind.Object &&
                   providerRoot.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String &&
                   string.Equals(
                       error.GetString(),
                       "durable_grant_mismatch",
                       StringComparison.Ordinal) &&
                   providerRoot.TryGetProperty("error_code", out var errorCode) &&
                   errorCode.ValueKind == JsonValueKind.Number &&
                   errorCode.TryGetInt32(out var numericErrorCode) &&
                   numericErrorCode == 9009;
        }
        catch (JsonException)
        {
            return false;
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

            // The body/message are relayed from the upstream platform and surface into the
            // registration error string (which is logged); scrub secret-shaped content so a
            // misbehaving upstream cannot echo a submitted secret back into our logs.
            return $"nyx_status={status}" +
                   (string.IsNullOrWhiteSpace(body) ? string.Empty : $" body={SecretScrubber.Scrub(body)}") +
                   (string.IsNullOrWhiteSpace(message) ? string.Empty : $" message={SecretScrubber.Scrub(message)}");
        }
        catch (JsonException)
        {
            return "invalid_error_envelope";
        }
    }

    /// <summary>
    /// Best-effort delete of a Nyx resource during provisioning rollback. An empty response is
    /// NyxID's successful 204 delete result. Error-envelope and exception cases are logged and
    /// never re-thrown, so a failed rollback never shadows the original provisioning failure.
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
            if (!string.IsNullOrWhiteSpace(response) && LooksLikeErrorEnvelope(response))
            {
                logger.LogWarning(
                    "Nyx rollback returned an error envelope: type={ResourceType}, id={ResourceId}, failureCode={FailureCode}",
                    resourceType,
                    resourceId,
                    "remote_delete_failed");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Nyx rollback failed: type={ResourceType}, id={ResourceId}, failureCode={FailureCode}, failureType={FailureType}",
                resourceType,
                resourceId,
                "remote_delete_exception",
                ex.GetType().Name);
        }
    }

    /// <summary>
    /// Returns the stable public error code for a provisioning failure. Provider response bodies,
    /// exception messages, secret references, and other diagnostic text are never returned.
    /// </summary>
    public static string SanitizeFailureReason(Exception ex) =>
        NormalizePublicFailureReason(ex is InvalidOperationException ? ex.Message : null);

    /// <summary>
    /// Defensively normalizes an adapter failure before it crosses an HTTP or tool boundary.
    /// </summary>
    public static string NormalizePublicFailureReason(string? reason)
    {
        var normalized = reason?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "provisioning_failed";

        foreach (var publicCode in PublicProvisioningFailureCodes)
        {
            if (string.Equals(normalized, publicCode, StringComparison.Ordinal) ||
                normalized.StartsWith($"{publicCode} ", StringComparison.Ordinal))
            {
                return publicCode;
            }
        }

        return "provisioning_failed";
    }

    private static readonly string[] PublicProvisioningFailureCodes =
    [
        "missing_access_token",
        "missing_nyx_channel_bot_id",
        "invalid_channel_bot_detail",
        "channel_bot_platform_mismatch",
        "channel_bot_not_found_or_forbidden",
        "channel_bot_not_adoptable",
        "missing_webhook_base_url",
        "missing_scope_id",
        "insecure_webhook_base_url",
        "channel_authorization_contract_invalid",
        "secret_vault_unavailable",
        "service_owner_forbidden",
        "nyxid_user_service_not_accessible",
        "scope_plan_changed",
        "nyxid_scope_plan_unavailable",
        "nyx_base_url_not_configured",
        "nyx_api_base_url_not_configured",
        "registration_id_already_exists",
        "invalid_runtime_config",
        "channel_bot_already_bound",
        "local_mirror_dispatch_failed",
        "local_mirror_accepted_remote_cleanup_skipped",
        "local_mirror_acceptance_unknown_remote_cleanup_skipped",
        "channel_agent_key_write_gate_closed",
        "ambiguous_channel_bot_route",
        "channel_route_not_accessible",
        "service_allowlist_not_supported",
        "provisioning_failed",
    ];
}
