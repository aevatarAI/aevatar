using System.Net;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Helpers;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

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
    /// Returns the trimmed per-connection proxy slug from a Nyx <c>POST /api/v1/keys</c>
    /// (connect-service) response — <c>slug</c> preferred, <c>proxy_url_slug</c> normalized as
    /// fallback — or <c>null</c> when the response is an error envelope, unparseable, or carries
    /// neither field.
    /// NyxID auto-numbers a base slug that is already taken (<c>api-lark-bot</c> →
    /// <c>api-lark-bot-2</c>/<c>-3</c>), so a user with several Lark bots gets a distinct proxy
    /// service per app. Capturing the slug NyxID actually assigned is what lets a later reply proxy
    /// through the SAME Lark app this connection was created for, instead of always the first one.
    /// </summary>
    public static string? ExtractOptionalProxyUrlSlug(string response)
    {
        if (LooksLikeErrorEnvelope(response))
            return null;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            return NormalizeProxyUrlSlug(ReadNonEmptyString(root, "slug")) ??
                   NormalizeProxyUrlSlug(ReadNonEmptyString(root, "proxy_url_slug"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the ids of every Lark channel-bot in a Nyx <c>GET /api/v1/channel-bots</c> list
    /// response (each list item carries <c>id</c> + <c>platform</c>, but NOT <c>platform_bot_id</c>:
    /// the per-app identifier lives only on the per-bot detail <c>GET /channel-bots/{id}</c>). The
    /// caller fetches each returned bot's detail and matches <c>platform_bot_id</c> against the Lark
    /// app being (re-)registered via <see cref="ChannelBotDetailMatchesApp"/>, so only the conflicting
    /// app's bot is deleted before the create retry — NyxID rejects a second channel-bot for an app
    /// that already has one with <c>409 already-exists</c>, which otherwise aborts a re-bind (the 502
    /// the /channels wizard surfaced). Returns an empty list when the response is an error envelope,
    /// unparseable, or carries no Lark bot.
    /// </summary>
    public static IReadOnlyList<string> ExtractLarkChannelBotIds(string listResponse)
    {
        if (LooksLikeErrorEnvelope(listResponse))
            return Array.Empty<string>();

        try
        {
            using var document = JsonDocument.Parse(listResponse);
            if (!TryGetBotArray(document.RootElement, out var bots))
                return Array.Empty<string>();

            var ids = new List<string>();
            foreach (var bot in bots.EnumerateArray())
            {
                if (bot.ValueKind != JsonValueKind.Object)
                    continue;

                var platform = ReadNonEmptyString(bot, "platform");
                if (!string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase))
                    continue;

                var id = ReadNonEmptyString(bot, "id");
                if (id is not null)
                    ids.Add(id);
            }

            return ids;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Returns true when a Nyx per-bot detail <c>GET /api/v1/channel-bots/{id}</c> response is for
    /// the Lark app <paramref name="appId"/>, i.e. its <c>platform_bot_id</c> equals the app id.
    /// This is the field the list response omits, so the conflicting-bot match must be made against
    /// the detail. Returns false for an error envelope, an unparseable body, a non-Lark bot, or any
    /// other app, so cleanup never deletes a bot belonging to a different Lark app.
    /// </summary>
    public static bool ChannelBotDetailMatchesApp(string detailResponse, string appId)
    {
        var normalizedAppId = appId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedAppId) || LooksLikeErrorEnvelope(detailResponse))
            return false;

        try
        {
            using var document = JsonDocument.Parse(detailResponse);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var platform = ReadNonEmptyString(root, "platform");
            if (!string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase))
                return false;

            return string.Equals(ReadNonEmptyString(root, "platform_bot_id"), normalizedAppId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetBotArray(JsonElement root, out JsonElement array)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
            return true;
        }

        foreach (var key in new[] { "data", "channel_bots", "channelBots", "items", "bots" })
        {
            if (root.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.Array)
            {
                array = element;
                return true;
            }
        }

        array = default;
        return false;
    }

    private static string? NormalizeProxyUrlSlug(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        var extracted = ExtractProxySlugFromRouteTemplate(trimmed);
        if (!string.IsNullOrWhiteSpace(extracted))
            return extracted;

        return LooksLikeProxyUrlTemplate(trimmed) ? null : trimmed;
    }

    private static string? ExtractProxySlugFromRouteTemplate(string value)
    {
        var path = Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.AbsolutePath : value;
        const string marker = "/proxy/s/";
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var start = index + marker.Length;
        var end = path.IndexOfAny(new[] { '/', '?', '#' }, start);
        var segment = end < 0 ? path[start..] : path[start..end];
        string slug;
        try
        {
            slug = Uri.UnescapeDataString(segment).Trim();
        }
        catch (UriFormatException)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(slug) || LooksLikeProxyUrlTemplate(slug) ? null : slug;
    }

    private static bool LooksLikeProxyUrlTemplate(string value) =>
        value.Contains("://", StringComparison.Ordinal) ||
        value.Contains('/', StringComparison.Ordinal) ||
        value.Contains('\\', StringComparison.Ordinal) ||
        value.Contains('?', StringComparison.Ordinal) ||
        value.Contains('#', StringComparison.Ordinal);

    private static string? ReadNonEmptyString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
            return null;

        var value = element.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
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

        if (normalized.StartsWith("channel_bot_id_request_failed ", StringComparison.Ordinal) &&
            (normalized.Contains("nyx_status=409", StringComparison.Ordinal) ||
             normalized.Contains("already registered", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("channel bot already exists", StringComparison.OrdinalIgnoreCase)))
        {
            return "channel_bot_already_exists";
        }

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
        "unsupported_platform",
        "missing_access_token",
        "missing_app_id",
        "missing_app_secret",
        "missing_verification_token",
        "missing_bot_token",
        "missing_webhook_base_url",
        "missing_scope_id",
        "insecure_webhook_base_url",
        "channel_authorization_contract_invalid",
        "secret_vault_unavailable",
        "service_owner_forbidden",
        "user_service_not_found",
        "scope_plan_changed",
        "nyxid_scope_plan_unavailable",
        "channel_service_connection_unavailable",
        "nyx_base_url_not_configured",
        "nyx_api_base_url_not_configured",
        "channel_bot_id_request_failed",
        "channel_bot_already_exists",
        "local_mirror_dispatch_failed",
        "local_mirror_accepted_remote_cleanup_skipped",
        "local_mirror_acceptance_unknown_remote_cleanup_skipped",
        "channel_agent_key_write_gate_closed",
        "service_allowlist_not_supported",
        "provisioning_failed",
    ];
}
