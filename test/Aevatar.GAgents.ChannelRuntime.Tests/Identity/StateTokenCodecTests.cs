using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Pins behaviour of the HMAC-sealed state token: round-trip, forged HMAC,
/// expired token, malformed/missing input. The state token is the only place
/// the PKCE verifier travels, so any drift here directly weakens OAuth code
/// interception defense (RFC 7636).
/// </summary>
public class StateTokenCodecTests
{
    private static NyxIdBrokerOptions Options() => new()
    {
        Authority = "https://nyxid.test",
        ClientId = "aevatar-channel-binding",
        ClientSecret = "client-secret",
        RedirectUri = "https://aevatar/api/oauth/nyxid-callback",
        StateTokenHmacKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        StateTokenKid = "v1",
        StateTokenLifetime = TimeSpan.FromMinutes(5),
    };

    private static ExternalSubjectRef SampleSubject() => new()
    {
        Platform = "lark",
        Tenant = "ou_tenant_x",
        ExternalUserId = "ou_user_y",
    };

    [Fact]
    public void Encode_DecodeRoundTrip_PreservesPayload()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-29T10:00:00Z"));
        var codec = new StateTokenCodec(Options(), clock);

        var token = codec.Encode("corr-1", SampleSubject(), "verifier-abc");
        var ok = codec.TryDecode(token, out var payload, out var errorCode);

        ok.Should().BeTrue();
        errorCode.Should().BeNull();
        payload.Should().NotBeNull();
        payload!.CorrelationId.Should().Be("corr-1");
        payload.PkceVerifier.Should().Be("verifier-abc");
        payload.ExternalSubject.ExternalUserId.Should().Be("ou_user_y");
    }

    [Fact]
    public void TryDecode_ReturnsExpiredAfterLifetime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-29T10:00:00Z"));
        var codec = new StateTokenCodec(Options(), clock);
        var token = codec.Encode("corr-1", SampleSubject(), "verifier-abc");

        clock.Advance(TimeSpan.FromMinutes(6));

        var ok = codec.TryDecode(token, out var payload, out var errorCode);

        ok.Should().BeFalse();
        payload.Should().BeNull();
        errorCode.Should().Be("state_expired");
    }

    [Fact]
    public void TryDecode_RejectsTamperedSignature()
    {
        var codec = new StateTokenCodec(Options(), new FakeTimeProvider(DateTimeOffset.UtcNow));
        var token = codec.Encode("corr-1", SampleSubject(), "verifier-abc");

        // Flip a byte in the HMAC segment by replacing the last char.
        var lastDot = token.LastIndexOf('.');
        var tampered = token[..(lastDot + 1)] + "xxxxxxxx";

        var ok = codec.TryDecode(tampered, out var payload, out var errorCode);

        ok.Should().BeFalse();
        payload.Should().BeNull();
        errorCode.Should().Be("state_signature_invalid");
    }

    [Fact]
    public void TryDecode_RejectsUnknownKid()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var encoder = new StateTokenCodec(Options(), clock);
        var verifier = new StateTokenCodec(
            new NyxIdBrokerOptions
            {
                Authority = "https://nyxid.test",
                ClientId = "aevatar-channel-binding",
                ClientSecret = "client-secret",
                RedirectUri = "https://aevatar/api/oauth/nyxid-callback",
                StateTokenHmacKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                StateTokenKid = "v2",
            },
            clock);

        var token = encoder.Encode("corr-1", SampleSubject(), "verifier-abc");
        var ok = verifier.TryDecode(token, out _, out var errorCode);

        ok.Should().BeFalse();
        errorCode.Should().Be("state_kid_unknown");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-state-token")]
    [InlineData("a.b")]
    public void TryDecode_RejectsMalformedTokens(string raw)
    {
        var codec = new StateTokenCodec(Options(), new FakeTimeProvider(DateTimeOffset.UtcNow));

        var ok = codec.TryDecode(raw, out var payload, out var errorCode);

        ok.Should().BeFalse();
        payload.Should().BeNull();
        errorCode.Should().NotBeNullOrEmpty();
    }
}
