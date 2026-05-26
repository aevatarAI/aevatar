using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Aevatar.Studio.Hosting.Controllers;

internal enum IfMatchStatus
{
    /// <summary>Header is not present or empty — caller did not request a precondition.</summary>
    Absent,

    /// <summary>Header parsed cleanly as a strong, single-version ETag.</summary>
    Valid,

    /// <summary>Header is present but malformed (weak validator, multiple values, wildcard, non-integer).</summary>
    Invalid,
}

internal static class ETagSupport
{
    private const string IfMatchHeader = "If-Match";

    /// <summary>
    /// Parse an If-Match header into a strong, single-value version.
    /// </summary>
    /// <remarks>
    /// Accepted forms: bare integer (<c>5</c>) or strong-quoted ETag (<c>"5"</c>).
    /// Rejected as <see cref="IfMatchStatus.Invalid"/>: weak validators (<c>W/"5"</c>),
    /// multi-value lists (<c>"5","6"</c>), wildcard (<c>*</c>), and any value that does
    /// not parse to a non-negative <see cref="long"/>. Distinguishing absent from invalid
    /// is essential — falling back to last-writer-wins on a malformed precondition would
    /// silently bypass the optimistic concurrency guarantee the caller asked for.
    /// </remarks>
    public static IfMatchStatus ParseIfMatch(HttpRequest request, out long version)
    {
        version = 0;

        if (!request.Headers.TryGetValue(IfMatchHeader, out var values) || values.Count == 0)
            return IfMatchStatus.Absent;

        var raw = values.ToString().Trim();
        if (raw.Length == 0)
            return IfMatchStatus.Absent;

        if (raw == "*")
            return IfMatchStatus.Invalid;

        if (raw.StartsWith("W/", StringComparison.Ordinal))
            return IfMatchStatus.Invalid;

        if (raw.Contains(','))
            return IfMatchStatus.Invalid;

        var unquoted = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"'
            ? raw[1..^1]
            : raw;

        if (unquoted.Length == 0)
            return IfMatchStatus.Invalid;

        if (long.TryParse(unquoted, out var parsed) && parsed >= 0)
        {
            version = parsed;
            return IfMatchStatus.Valid;
        }

        return IfMatchStatus.Invalid;
    }

    public static void WriteETag(HttpResponse response, long version)
    {
        response.Headers[HeaderNames.ETag] = $"\"{version}\"";
    }
}
