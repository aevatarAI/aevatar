namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Shared policy for whether a NyxID channel-bot relay <c>callback_url</c> is safe to register.
/// NyxID delivers each inbound relay callback to this URL carrying the short-lived
/// <c>X-NyxID-User-Token</c> (a first-party bearer credential), so a cleartext http callback would
/// ship that credential over the wire in the clear. Absolute https is accepted; http is accepted
/// only for loopback hosts (localhost / 127.0.0.1 / ::1) so local development keeps working.
///
/// Owned here — the lowest layer both callback-URL registration paths already depend on (the relay
/// provisioning services in Aevatar.GAgents.Channel.NyxIdRelay reference this project, and the
/// nyxid_api_keys tool lives here) — so both paths enforce one policy that cannot drift.
/// </summary>
public static class NyxRelayCallbackUrlPolicy
{
    /// <summary>
    /// True when <paramref name="callbackUrl"/> is an absolute https URL, or an http URL whose host
    /// is loopback. Everything else (cleartext public host, non-http(s) scheme, non-absolute URL) is
    /// rejected because the registered callback would leak the relay user token in the clear.
    /// </summary>
    public static bool IsSecureUrl(string? callbackUrl)
    {
        if (!Uri.TryCreate(callbackUrl?.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme == Uri.UriSchemeHttps)
            return true;

        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }
}
