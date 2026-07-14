using Aevatar.Authentication.Hosting;
using FluentAssertions;
using Xunit;

namespace Aevatar.Bootstrap.Tests;

public sealed class WebSocketSubprotocolTokenTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ1c2VyIn0.sig-nature_value";

    [Fact]
    public void ExtractBearer_ReturnsToken_WhenBearerSubprotocolPresent()
    {
        var protocols = new[] { WebSocketSubprotocolToken.VoiceSubprotocol, WebSocketSubprotocolToken.BearerPrefix + Jwt };

        WebSocketSubprotocolToken.ExtractBearer(protocols).Should().Be(Jwt);
    }

    [Fact]
    public void ExtractBearer_ReturnsTokenVerbatim_IncludingJwtDots()
    {
        // The token is carried after the prefix and must not be truncated at '.' (JWTs contain dots).
        var protocols = new[] { WebSocketSubprotocolToken.BearerPrefix + Jwt };

        WebSocketSubprotocolToken.ExtractBearer(protocols).Should().Be(Jwt);
    }

    [Fact]
    public void ExtractBearer_ReturnsNull_WhenOnlyNonSensitiveSubprotocol()
    {
        var protocols = new[] { WebSocketSubprotocolToken.VoiceSubprotocol };

        WebSocketSubprotocolToken.ExtractBearer(protocols).Should().BeNull();
    }

    [Fact]
    public void ExtractBearer_ReturnsNull_ForNullOrEmptyInput()
    {
        WebSocketSubprotocolToken.ExtractBearer(null).Should().BeNull();
        WebSocketSubprotocolToken.ExtractBearer(System.Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void ExtractBearer_ReturnsNull_WhenPrefixHasNoToken()
    {
        WebSocketSubprotocolToken.ExtractBearer(new[] { "aevatar-bearer." }).Should().BeNull();
        WebSocketSubprotocolToken.ExtractBearer(new[] { "aevatar-bearer.   " }).Should().BeNull();
    }

    [Fact]
    public void ExtractBearer_TrimsSurroundingWhitespace()
    {
        WebSocketSubprotocolToken.ExtractBearer(new[] { "aevatar-bearer.  " + Jwt + "  " }).Should().Be(Jwt);
    }

    [Fact]
    public void ExtractBearer_IgnoresEmptyEntriesAndPicksBearer()
    {
        var protocols = new[] { "", WebSocketSubprotocolToken.VoiceSubprotocol, WebSocketSubprotocolToken.BearerPrefix + Jwt };

        WebSocketSubprotocolToken.ExtractBearer(protocols).Should().Be(Jwt);
    }

    [Fact]
    public void SelectVoiceSubprotocol_ReturnsOnlyFixedNonSensitiveProtocol()
    {
        var protocols = new[]
        {
            WebSocketSubprotocolToken.BearerPrefix + Jwt,
            WebSocketSubprotocolToken.VoiceSubprotocol,
        };

        WebSocketSubprotocolToken.SelectVoiceSubprotocol(protocols)
            .Should().Be(WebSocketSubprotocolToken.VoiceSubprotocol);
    }

    [Fact]
    public void SelectVoiceSubprotocol_DoesNotEchoBearerOrInventLegacyProtocol()
    {
        WebSocketSubprotocolToken.SelectVoiceSubprotocol(
                new[] { WebSocketSubprotocolToken.BearerPrefix + Jwt })
            .Should().BeNull();
        WebSocketSubprotocolToken.SelectVoiceSubprotocol(null).Should().BeNull();
        WebSocketSubprotocolToken.SelectVoiceSubprotocol(Array.Empty<string>()).Should().BeNull();
    }
}
