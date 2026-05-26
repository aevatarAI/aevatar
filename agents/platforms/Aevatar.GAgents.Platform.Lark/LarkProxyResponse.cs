using System.Text.Json;

namespace Aevatar.GAgents.Platform.Lark;

/// <summary>
/// Inspects response bodies returned by NyxIdApiClient.ProxyRequestAsync for downstream
/// Lark API calls. The proxy is two-layered:
///
/// <list type="bullet">
/// <item><description>HTTP 200 from NyxID may still carry a Lark business error at the top
/// level: <c>{"code": &lt;non-zero&gt;, "msg": "..."}</c>.</description></item>
/// <item><description>HTTP non-2xx from NyxID is wrapped by <c>NyxIdApiClient.SendAsync</c>
/// (NyxIdApiClient.cs:680) as <c>{"error": true, "status": &lt;http&gt;, "body": "&lt;raw
/// downstream body&gt;"}</c>. The <c>body</c> is a STRING containing the raw Lark JSON, so
/// the Lark business code (e.g. <c>99992364 user id cross tenant</c> on HTTP 400, or
/// <c>230002 bot not in chat</c> on HTTP 400) is nested INSIDE the string and must be parsed
/// from there before <c>larkCode</c>-gated branches can fire.</description></item>
/// <item><description>Network/exception path uses <c>{"error": true, "message": "..."}</c>
/// (no status, no body).</description></item>
/// </list>
///
/// Callers that ignore the result silently drop all three classes of failure, which is what
/// motivates this helper. Reviewer (PR #412 r3141700469) verbatim:
/// <code>
/// production failures arrive through NyxIdApiClient.SendAsync as an HTTP-400 Nyx envelope:
/// {"error": true, "status": 400, "body": "{\"code\":99992364,...}"}.
/// LarkProxyResponse.TryGetError currently returns true for that shape but leaves
/// larkCode=null because it does not parse the nested body.
/// </code>
/// </summary>
public static class LarkProxyResponse
{
    /// <summary>
    /// Returns true when the response body indicates a downstream failure. <paramref name="larkCode"/>
    /// is set whenever a Lark business code can be extracted — top-level for HTTP-200
    /// responses, OR nested in the <c>body</c> string for HTTP-non-2xx wrapped envelopes.
    /// <paramref name="detail"/> is a short human-readable summary suitable for log lines or
    /// exception messages.
    ///
    /// <para><b>Branch order (invariant):</b> top-level <c>code</c> is checked BEFORE the
    /// <c>error</c> envelope. The two production shapes are mutually exclusive today
    /// (HTTP-200 → top-level <c>code</c> only; HTTP-non-2xx → <c>{error:true,status,body}</c>
    /// only — <c>SendAsync</c> never emits both at the same level), so for every observed
    /// response either order yields the same result. The order is fixed deliberately for
    /// forward-compat: if NyxID ever wraps a successful Lark business rejection as a hybrid
    /// <c>{"error":true,"status":200,"code":230002,...}</c>, this prioritizes the explicit
    /// Lark business code over the generic "nyx says error" envelope. The branch ordering
    /// reversed in PR #412 (was: <c>error</c> first, then top-level <c>code</c>); reviewer
    /// (PR #412 long-form review §4) flagged the implicit reversal — capturing the rationale
    /// here so future readers do not "fix" it back.</para>
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

            // Top-level Lark business error: HTTP 200 from NyxID, but Lark embedded a non-zero
            // code (e.g. `code:231002 no permission to react` on a successful HTTP transport).
            if (root.TryGetProperty("code", out var topCodeProperty) &&
                topCodeProperty.ValueKind == JsonValueKind.Number &&
                topCodeProperty.TryGetInt32(out var topCode) &&
                topCode != 0)
            {
                larkCode = topCode;
                detail = TryReadString(root, "msg") ?? $"code={topCode}";
                return true;
            }

            if (root.TryGetProperty("error", out var errorProperty))
            {
                var hasErrorFlag = errorProperty.ValueKind == JsonValueKind.True ||
                                   (errorProperty.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(errorProperty.GetString()));
                if (!hasErrorFlag)
                    return false;

                // Nyx HTTP-non-2xx wrapper: try to recover the nested Lark code/msg from the
                // `body` string. Reviewer (PR #412 r3141700469) called this out — without
                // nested parsing, every HTTP-400 Lark business error (which is the common
                // production case) hits this path with `larkCode=null` and the gated branches
                // (BotNotInChat retry, UserIdCrossTenant hint) never fire.
                if (TryParseNestedLarkBody(root, out var nestedCode, out var nestedDetail))
                {
                    larkCode = nestedCode;
                    detail = nestedDetail;
                    return true;
                }

                if (errorProperty.ValueKind == JsonValueKind.True)
                {
                    detail = TryReadString(root, "message")
                             ?? TryReadString(root, "body")
                             ?? FormatStatusFallback(root)
                             ?? "proxy_error";
                    return true;
                }

                if (errorProperty.ValueKind == JsonValueKind.String)
                {
                    detail = errorProperty.GetString()!.Trim();
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Best-effort detection: bodies that are not valid JSON are treated as a non-error
            // (the caller decides what to do with them).
        }

        return false;
    }

    /// <summary>
    /// Parses <c>{"error":true,"status":400,"body":"&lt;raw json string&gt;"}</c> shapes — the
    /// envelope <c>NyxIdApiClient.SendAsync</c> produces for HTTP-non-2xx responses. When the
    /// nested body is a JSON object with <c>code != 0</c>, returns the Lark code and a
    /// `code=N: msg` detail so callers see the actual upstream rejection.
    /// </summary>
    private static bool TryParseNestedLarkBody(JsonElement root, out int? larkCode, out string detail)
    {
        larkCode = null;
        detail = string.Empty;

        var rawBody = TryReadString(root, "body");
        if (string.IsNullOrEmpty(rawBody))
            return false;

        try
        {
            using var nested = JsonDocument.Parse(rawBody);
            var nestedRoot = nested.RootElement;
            if (nestedRoot.ValueKind != JsonValueKind.Object)
                return false;

            if (!nestedRoot.TryGetProperty("code", out var codeProperty) ||
                codeProperty.ValueKind != JsonValueKind.Number ||
                !codeProperty.TryGetInt32(out var code) ||
                code == 0)
                return false;

            larkCode = code;
            var msg = TryReadString(nestedRoot, "msg") ?? $"code={code}";
            // Carry the Nyx HTTP status alongside the Lark code so log lines and exception
            // messages preserve enough context to correlate with NyxIdApiClient warnings.
            var status = TryReadInt32(root, "status");
            detail = status is { } s
                ? $"nyx_status={s} lark_code={code} msg={msg}"
                : $"lark_code={code} msg={msg}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FormatStatusFallback(JsonElement root)
    {
        var status = TryReadInt32(root, "status");
        return status is { } s ? $"nyx_status={s}" : null;
    }

    private static int? TryReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            return null;
        }
        return value;
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
