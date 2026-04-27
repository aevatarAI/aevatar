using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Aevatar.Studio.Hosting.Controllers;

internal static class ETagSupport
{
    private const string IfMatchHeader = "If-Match";
    private const string ETagHeader = "ETag";

    /// <summary>
    /// Parse an If-Match header into a long version. Accepts both quoted ETag form
    /// (<c>"5"</c>) and bare integer (<c>5</c>). Returns null when missing or unparseable.
    /// </summary>
    public static long? TryParseIfMatch(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(IfMatchHeader, out var values) || values.Count == 0)
            return null;

        var raw = values.ToString().Trim();
        if (string.IsNullOrEmpty(raw))
            return null;

        var unquoted = raw.Trim('"');
        return long.TryParse(unquoted, out var version) ? version : null;
    }

    public static void WriteETag(HttpResponse response, long version)
    {
        response.Headers[HeaderNames.ETag] = $"\"{version}\"";
    }
}
