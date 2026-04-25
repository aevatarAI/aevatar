using System.Text.Json;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Inspects response bodies returned by NyxIdApiClient.ProxyRequestAsync for downstream
/// Lark API calls. The proxy is two-layered: HTTP non-2xx from NyxID gets packaged into a
/// Nyx error envelope (<c>{"error": true, "message": "..."}</c> or <c>{"error": "..."}</c>),
/// and HTTP 200 from NyxID may still carry a Lark business error
/// (<c>{"code": &lt;non-zero&gt;, "msg": "..."}</c>). Callers that ignore the result silently
/// drop both classes of failure, which is what motivates this helper.
/// </summary>
internal static class LarkProxyResponse
{
    /// <summary>
    /// Returns true when the response body indicates a downstream failure. <paramref name="larkCode"/>
    /// is set only for the Lark business-error path so callers can selectively gate logging on
    /// known recurring config gaps (e.g. 231002 = no permission to react). <paramref name="detail"/>
    /// is a short human-readable summary suitable for log lines or exception messages.
    /// </summary>
    public static bool TryGetError(string? response, out int? larkCode, out string detail)
    {
        larkCode = null;
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("error", out var errorProperty))
            {
                if (errorProperty.ValueKind == JsonValueKind.True)
                {
                    detail = TryReadString(root, "message")
                             ?? TryReadString(root, "body")
                             ?? "proxy_error";
                    return true;
                }

                if (errorProperty.ValueKind == JsonValueKind.String)
                {
                    var error = errorProperty.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        detail = error;
                        return true;
                    }
                }
            }

            if (root.TryGetProperty("code", out var codeProperty) &&
                codeProperty.ValueKind == JsonValueKind.Number &&
                codeProperty.TryGetInt32(out var code) &&
                code != 0)
            {
                larkCode = code;
                detail = TryReadString(root, "msg") ?? $"code={code}";
                return true;
            }
        }
        catch (JsonException)
        {
            // Best-effort detection: bodies that are not valid JSON are treated as a non-error
            // (the caller decides what to do with them).
        }

        return false;
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
