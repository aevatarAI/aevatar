using System.Text.Json;
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
    /// Returns the one-time <c>full_key</c> from a Nyx create-api-key response, or <c>null</c>
    /// when the response is an error envelope, unparseable, or carries no full_key. NyxID returns
    /// the raw key material exactly once at creation; the caller must hand it straight to the
    /// secret vault and never persist or log it.
    /// </summary>
    public static string? ExtractOptionalApiKeyFullKey(string response)
    {
        if (LooksLikeErrorEnvelope(response))
            return null;

        try
        {
            using var document = JsonDocument.Parse(response);
            return ReadNonEmptyString(document.RootElement, "full_key");
        }
        catch (JsonException)
        {
            return null;
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
                    SecretScrubber.Scrub(response));
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
