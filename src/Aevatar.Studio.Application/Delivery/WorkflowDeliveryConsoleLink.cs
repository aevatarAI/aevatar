namespace Aevatar.Studio.Application.Delivery;

/// <summary>
/// Builds the product-console link a delivery customer opens after an installation
/// exists. The delivery read model owns only the console-relative member invoke path;
/// the console-web origin is host configuration, because this API is served from a
/// different host than <c>apps/aevatar-console-web</c>. Resolving the relative path
/// against the API origin produces a link that does not exist, so an unconfigured or
/// invalid origin yields no link at all.
/// </summary>
public static class WorkflowDeliveryConsoleLink
{
    /// <summary>
    /// Accepts an absolute HTTPS origin, or an absolute HTTP origin when it is loopback.
    /// Userinfo, query, and fragment are rejected so the stored value can only ever
    /// contribute an origin and an optional path base.
    /// </summary>
    public static bool TryNormalizeBaseUrl(string? value, out Uri normalized)
    {
        normalized = null!;
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate))
            return false;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        var isHttps = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp = string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            parsed.IsLoopback;
        if (!isHttps && !isLoopbackHttp)
            return false;

        normalized = parsed;
        return true;
    }

    /// <summary>
    /// Returns the absolute console URL for invoking one provisioned member, or
    /// <see langword="null"/> when either the console origin or the member identity is
    /// unavailable. The route targets the published member invoke workbench rather than
    /// the draft workflow editor.
    /// </summary>
    public static string? BuildMemberInvokeUrl(
        Uri? consoleWebBaseUri,
        string? scopeId,
        string? teamId,
        string? memberId)
    {
        if (consoleWebBaseUri is null ||
            string.IsNullOrWhiteSpace(scopeId) ||
            string.IsNullOrWhiteSpace(teamId) ||
            string.IsNullOrWhiteSpace(memberId))
        {
            return null;
        }

        var basePath = consoleWebBaseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var relativePath =
            $"/scopes/{Uri.EscapeDataString(scopeId)}/teams/{Uri.EscapeDataString(teamId)}" +
            $"/members/{Uri.EscapeDataString(memberId)}/invoke";
        return basePath + relativePath;
    }
}
