namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>
/// Builds and validates the relay callback URL that aevatar registers with NyxID as a
/// channel bot's <c>callback_url</c>. NyxID delivers each inbound relay callback to this URL
/// carrying the short-lived <c>X-NyxID-User-Token</c> (a first-party bearer credential), so the
/// URL MUST be https to keep that credential off cleartext transports. Loopback hosts are exempt
/// so local development against <c>http://localhost</c> keeps working.
/// </summary>
internal static class NyxRelayCallbackUrl
{
    private const string RelayCallbackPath = "/api/webhooks/nyxid-relay";

    /// <summary>
    /// True when <paramref name="webhookBaseUrl"/> is an absolute https URL, or an http URL whose
    /// host is loopback (localhost / 127.0.0.1 / ::1). Everything else is rejected because the
    /// registered callback would ship the relay user token over the wire in the clear.
    /// </summary>
    public static bool IsSecureBaseUrl(string? webhookBaseUrl)
    {
        if (!Uri.TryCreate(webhookBaseUrl?.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme == Uri.UriSchemeHttps)
            return true;

        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    /// <summary>
    /// Builds the relay callback URL from an already-validated <paramref name="webhookBaseUrl"/>.
    /// Callers must gate on <see cref="IsSecureBaseUrl"/> first.
    /// </summary>
    public static string Build(string webhookBaseUrl) =>
        $"{webhookBaseUrl.Trim().TrimEnd('/')}{RelayCallbackPath}";
}
