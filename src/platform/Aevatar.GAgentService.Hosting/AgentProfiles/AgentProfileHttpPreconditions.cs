using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgentService.Hosting.AgentProfiles;

internal static class AgentProfileHttpPreconditions
{
    private const string ETagPrefix = "\"agent-profile-v";

    public static string Format(long version) =>
        $"\"agent-profile-v{version.ToString(CultureInfo.InvariantCulture)}\"";

    public static bool TryRequireIfMatch(
        HttpContext http,
        out long expectedAuthorityStateVersion,
        out IResult rejected)
    {
        ArgumentNullException.ThrowIfNull(http);
        expectedAuthorityStateVersion = 0;

        if (!http.Request.Headers.ContainsKey("If-Match"))
        {
            rejected = AgentProfileHttpResults.Error(
                StatusCodes.Status428PreconditionRequired,
                "AGENT_PROFILE_IF_MATCH_REQUIRED");
            return false;
        }

        var values = http.Request.Headers.IfMatch;
        if (values.Count != 1 || !TryParse(values[0], out expectedAuthorityStateVersion))
        {
            rejected = AgentProfileHttpResults.Error(
                StatusCodes.Status400BadRequest,
                "INVALID_AGENT_PROFILE_IF_MATCH");
            return false;
        }

        rejected = Results.Empty;
        return true;
    }

    public static bool TryReadRequiredIdempotencyKey(
        HttpContext http,
        out string idempotencyKey,
        out IResult rejected)
    {
        if (!TryReadIdempotencyKey(http, required: true, out var value, out rejected))
        {
            idempotencyKey = string.Empty;
            return false;
        }

        idempotencyKey = value!;
        return true;
    }

    public static bool TryReadOptionalIdempotencyKey(
        HttpContext http,
        out string? idempotencyKey,
        out IResult rejected) =>
        TryReadIdempotencyKey(http, required: false, out idempotencyKey, out rejected);

    private static bool TryReadIdempotencyKey(
        HttpContext http,
        bool required,
        out string? idempotencyKey,
        out IResult rejected)
    {
        ArgumentNullException.ThrowIfNull(http);
        idempotencyKey = null;
        if (!http.Request.Headers.TryGetValue("Idempotency-Key", out var values))
        {
            if (!required)
            {
                rejected = Results.Empty;
                return true;
            }

            rejected = AgentProfileHttpResults.Error(
                StatusCodes.Status400BadRequest,
                "IDEMPOTENCY_KEY_REQUIRED");
            return false;
        }

        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            rejected = AgentProfileHttpResults.Error(
                StatusCodes.Status400BadRequest,
                "INVALID_IDEMPOTENCY_KEY");
            return false;
        }

        idempotencyKey = values[0];
        rejected = Results.Empty;
        return true;
    }

    private static bool TryParse(string? wireValue, out long version)
    {
        version = 0;
        if (string.IsNullOrEmpty(wireValue) ||
            !wireValue.StartsWith(ETagPrefix, StringComparison.Ordinal) ||
            !wireValue.EndsWith('"'))
        {
            return false;
        }

        var decimalValue = wireValue.AsSpan(ETagPrefix.Length, wireValue.Length - ETagPrefix.Length - 1);
        if (decimalValue.IsEmpty ||
            (decimalValue.Length > 1 && decimalValue[0] == '0') ||
            decimalValue.IndexOfAnyExceptInRange('0', '9') >= 0 ||
            !long.TryParse(decimalValue, NumberStyles.None, CultureInfo.InvariantCulture, out version))
        {
            return false;
        }

        return string.Equals(Format(version), wireValue, StringComparison.Ordinal);
    }
}
