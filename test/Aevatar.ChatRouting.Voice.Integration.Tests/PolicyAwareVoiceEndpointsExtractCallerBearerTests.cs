using Aevatar.Authentication.Hosting;
using Aevatar.Mainnet.Host.Api.Voice;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Aevatar.ChatRouting.Voice.Integration.Tests;

/// <summary>
/// M5 — the voice caller-bearer extractor must PREFER the token carried in the
/// <c>Sec-WebSocket-Protocol</c> handshake header (<c>aevatar-bearer.&lt;token&gt;</c>)
/// so the credential never lands in the request URL (URL → stdout → Elasticsearch
/// leak). It must still fall back to the Authorization header and, last, the
/// legacy <c>?access_token=</c> query param so older clients keep working.
/// </summary>
public sealed class PolicyAwareVoiceEndpointsExtractCallerBearerTests
{
    private const string SubprotocolJwt = "eyJhbGciOiJIUzI1NiJ9.subprotocol-token.sig_value";
    private const string HeaderJwt = "eyJhbGciOiJIUzI1NiJ9.authorization-token.sig_value";
    private const string QueryJwt = "eyJhbGciOiJIUzI1NiJ9.query-token.sig_value";

    [Fact]
    public void PrefersSubprotocolToken_OverAuthorizationHeaderAndQueryParam()
    {
        var http = new DefaultHttpContext();
        SetRequestedSubprotocols(http, WebSocketSubprotocolToken.VoiceSubprotocol, WebSocketSubprotocolToken.BearerPrefix + SubprotocolJwt);
        http.Request.Headers.Authorization = "Bearer " + HeaderJwt;
        http.Request.QueryString = QueryString.Create("access_token", QueryJwt);

        PolicyAwareVoiceEndpoints.ExtractCallerBearer(http).Should().Be(SubprotocolJwt);
    }

    [Fact]
    public void FallsBackToAuthorizationHeader_WhenNoSubprotocolToken()
    {
        var http = new DefaultHttpContext();
        // Only the non-sensitive subprotocol is offered — no bearer subprotocol.
        SetRequestedSubprotocols(http, WebSocketSubprotocolToken.VoiceSubprotocol);
        http.Request.Headers.Authorization = "Bearer " + HeaderJwt;
        http.Request.QueryString = QueryString.Create("access_token", QueryJwt);

        PolicyAwareVoiceEndpoints.ExtractCallerBearer(http).Should().Be(HeaderJwt);
    }

    [Fact]
    public void FallsBackToQueryParam_WhenNoSubprotocolAndNoAuthorizationHeader()
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = QueryString.Create("access_token", QueryJwt);

        PolicyAwareVoiceEndpoints.ExtractCallerBearer(http).Should().Be(QueryJwt);
    }

    [Fact]
    public void PrefersSubprotocolToken_EvenWhenLegacyQueryParamAlsoPresent()
    {
        // Guards the M5 objective: a client that still appends ?access_token=
        // but ALSO offers the subprotocol must have the subprotocol win, so the
        // URL token is never the one used.
        var http = new DefaultHttpContext();
        SetRequestedSubprotocols(http, WebSocketSubprotocolToken.BearerPrefix + SubprotocolJwt);
        http.Request.QueryString = QueryString.Create("access_token", QueryJwt);

        PolicyAwareVoiceEndpoints.ExtractCallerBearer(http).Should().Be(SubprotocolJwt);
    }

    [Fact]
    public void ReturnsNull_WhenNoTokenAnywhere()
    {
        var http = new DefaultHttpContext();

        PolicyAwareVoiceEndpoints.ExtractCallerBearer(http).Should().BeNull();
    }

    // WebSocketManager.WebSocketRequestedProtocols is sourced from the
    // Sec-WebSocket-Protocol request header (comma-separated), so setting that
    // header is the faithful wire representation the extractor reads.
    private static void SetRequestedSubprotocols(HttpContext http, params string[] protocols) =>
        http.Request.Headers[HeaderNames.SecWebSocketProtocol] = string.Join(", ", protocols);
}
