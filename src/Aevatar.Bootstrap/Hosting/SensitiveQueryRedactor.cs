using System;
using System.Collections.Generic;

namespace Aevatar.Bootstrap.Hosting;

/// <summary>
/// Redacts the values of known-sensitive query-string parameters before a
/// request URL is written to logs.
/// <para>
/// WebSocket clients cannot set an <c>Authorization</c> header, so the voice
/// transport authenticates with <c>/ws/voice?access_token=&lt;JWT&gt;</c>. ASP.NET
/// Core's request logging writes the full URL — including that token — to stdout,
/// which is shipped to Elasticsearch, leaking a usable NyxID access token. This
/// helper is the single place that decides which parameters must never appear in
/// a logged URL.
/// </para>
/// </summary>
public static class SensitiveQueryRedactor
{
    public const string RedactionPlaceholder = "REDACTED";

    // Case-insensitive. Covers the WebSocket auth token plus the common shapes of
    // any credential that could ride a query string (OAuth codes, API keys, etc.).
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token",
        "token",
        "id_token",
        "refresh_token",
        "api_key",
        "apikey",
        "key",
        "client_secret",
        "secret",
        "password",
        "pwd",
        "code",
        "sig",
        "signature",
        "bearer",
        "authorization",
    };

    /// <summary>
    /// Returns <paramref name="queryString"/> with the values of sensitive
    /// parameters replaced by <see cref="RedactionPlaceholder"/>. The input may
    /// include the leading '?'. Ordering, structure and non-sensitive parameters
    /// are preserved. Null/empty inputs are returned unchanged.
    /// </summary>
    public static string Redact(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
            return queryString ?? string.Empty;

        var hasPrefix = queryString[0] == '?';
        var body = hasPrefix ? queryString[1..] : queryString;
        if (body.Length == 0)
            return queryString;

        var pairs = body.Split('&');
        var changed = false;
        for (var i = 0; i < pairs.Length; i++)
        {
            var pair = pairs[i];
            if (pair.Length == 0)
                continue;

            var eq = pair.IndexOf('=');
            if (eq < 0)
                continue; // bare flag, no value to redact

            var key = pair[..eq];
            if (!IsSensitive(key))
                continue;

            pairs[i] = key + "=" + RedactionPlaceholder;
            changed = true;
        }

        if (!changed)
            return queryString;

        var redacted = string.Join("&", pairs);
        return hasPrefix ? "?" + redacted : redacted;
    }

    private static bool IsSensitive(string key)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(key);
        }
        catch (UriFormatException)
        {
            decoded = key;
        }

        return SensitiveKeys.Contains(decoded);
    }
}
