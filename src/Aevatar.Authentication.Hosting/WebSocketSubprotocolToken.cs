using System;
using System.Collections.Generic;

namespace Aevatar.Authentication.Hosting;

/// <summary>
/// Carries the bearer token for WebSocket authentication in the
/// <c>Sec-WebSocket-Protocol</c> handshake header instead of the URL query string.
/// <para>
/// Browsers cannot set an <c>Authorization</c> header on a WebSocket handshake, so
/// the voice transport historically authenticated with
/// <c>/ws/voice?access_token=&lt;JWT&gt;</c>. That put a usable token in the request
/// URL, which the host logs to stdout → Elasticsearch (and any ingress/LB access log)
/// — a credential-harvest surface. Browsers <em>can</em> offer subprotocols via
/// <c>new WebSocket(url, [...])</c>, so we smuggle the token as one, mirroring the
/// Kubernetes apiserver's <c>base64url.bearer...</c> pattern. The token then travels
/// in a header and never appears in a URL.
/// </para>
/// <para>
/// The client offers two subprotocols: the non-sensitive <see cref="VoiceSubprotocol"/>
/// and <c>aevatar-bearer.&lt;token&gt;</c>. The server selects only the non-sensitive
/// voice protocol. This satisfies browser handshake validation without ever echoing
/// the bearer entry into response headers or logs.
/// </para>
/// </summary>
public static class WebSocketSubprotocolToken
{
    /// <summary>
    /// Prefix of the subprotocol entry that carries the bearer token:
    /// <c>aevatar-bearer.&lt;token&gt;</c>. JWTs are base64url (<c>A–Z a–z 0–9 - _ .</c>),
    /// all valid subprotocol-token characters, so the token is carried verbatim.
    /// </summary>
    public const string BearerPrefix = "aevatar-bearer.";

    /// <summary>
    /// Non-sensitive subprotocol the client also offers so a compliant handshake has a
    /// selectable protocol. Shared wire constant with the browser client
    /// (VoiceConsolePage.cs).
    /// </summary>
    public const string VoiceSubprotocol = "aevatar-voice-v1";

    /// <summary>
    /// Selects the fixed non-sensitive voice protocol when the client offered it.
    /// The bearer-carrying protocol is never eligible for the response handshake.
    /// </summary>
    public static string? SelectVoiceSubprotocol(IEnumerable<string>? requestedProtocols)
    {
        if (requestedProtocols is null)
            return null;

        foreach (var protocol in requestedProtocols)
        {
            if (string.Equals(protocol, VoiceSubprotocol, StringComparison.Ordinal))
                return VoiceSubprotocol;
        }

        return null;
    }

    /// <summary>
    /// Returns the bearer token carried in the requested subprotocols (the remainder
    /// after <see cref="BearerPrefix"/>), or <see langword="null"/> if none is present.
    /// </summary>
    public static string? ExtractBearer(IEnumerable<string>? requestedProtocols)
    {
        if (requestedProtocols is null)
            return null;

        foreach (var protocol in requestedProtocols)
        {
            if (string.IsNullOrEmpty(protocol) ||
                !protocol.StartsWith(BearerPrefix, StringComparison.Ordinal))
                continue;

            var token = protocol[BearerPrefix.Length..].Trim();
            if (!string.IsNullOrEmpty(token))
                return token;
        }

        return null;
    }
}
