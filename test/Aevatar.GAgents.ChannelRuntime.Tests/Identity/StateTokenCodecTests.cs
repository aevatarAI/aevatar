using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
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
    private static readonly byte[] HmacKey =
        Convert.FromHexString("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

    private static AevatarOAuthClientSnapshot Snapshot(byte[]? hmacKey = null, string hmacKid = "v1") => new(
        ClientId: "aevatar-channel-binding",
        ClientIdIssuedAt: DateTimeOffset.Parse("2026-04-29T09:00:00Z"),
        HmacKid: hmacKid,
        HmacKey: hmacKey ?? HmacKey,
        HmacKeyRotatedAt: DateTimeOffset.Parse("2026-04-29T09:00:00Z"),
        NyxIdAuthority: "https://nyxid.test",
        BrokerCapabilityObserved: true,
        BrokerCapabilityObservedAt: DateTimeOffset.Parse("2026-04-29T09:00:00Z"));

    private static ExternalSubjectRef SampleSubject() => new()
    {
        Platform = "lark",
        Tenant = "ou_tenant_x",
        ExternalUserId = "ou_user_y",
    };

    [Fact]
    public async Task Encode_DecodeRoundTrip_PreservesPayload()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-29T10:00:00Z"));
        var provider = new FakeOAuthClientProvider(Snapshot());
        var codec = new StateTokenCodec(provider, options: null, clock);

        var token = await codec.EncodeAsync("corr-1", SampleSubject(), "verifier-abc");
        var result = await codec.TryDecodeAsync(token);

        result.Succeeded.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.Payload.Should().NotBeNull();
        result.Payload!.CorrelationId.Should().Be("corr-1");
        result.Payload.PkceVerifier.Should().Be("verifier-abc");
        result.Payload.ExternalSubject.ExternalUserId.Should().Be("ou_user_y");
    }

    [Fact]
    public async Task TryDecode_ReturnsExpiredAfterLifetime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-29T10:00:00Z"));
        var provider = new FakeOAuthClientProvider(Snapshot());
        var codec = new StateTokenCodec(provider, options: null, clock);
        var token = await codec.EncodeAsync("corr-1", SampleSubject(), "verifier-abc");

        clock.Advance(TimeSpan.FromMinutes(6));

        var result = await codec.TryDecodeAsync(token);

        result.Succeeded.Should().BeFalse();
        result.Payload.Should().BeNull();
        result.ErrorCode.Should().Be("state_expired");
    }

    [Fact]
    public async Task TryDecode_RejectsTamperedSignature()
    {
        var provider = new FakeOAuthClientProvider(Snapshot());
        var codec = new StateTokenCodec(provider, options: null, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var token = await codec.EncodeAsync("corr-1", SampleSubject(), "verifier-abc");

        // Flip a byte in the HMAC segment by replacing the last char.
        var lastDot = token.LastIndexOf('.');
        var tampered = token[..(lastDot + 1)] + "xxxxxxxx";

        var result = await codec.TryDecodeAsync(tampered);

        result.Succeeded.Should().BeFalse();
        result.Payload.Should().BeNull();
        result.ErrorCode.Should().Be("state_signature_invalid");
    }

    [Fact]
    public async Task TryDecode_FailsWhenHmacKeyUnprovisioned()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var encoder = new StateTokenCodec(new FakeOAuthClientProvider(Snapshot()), options: null, clock);
        var token = await encoder.EncodeAsync("corr-1", SampleSubject(), "verifier-abc");

        // Verifier has no client provisioned yet.
        var verifier = new StateTokenCodec(new FakeOAuthClientProvider(snapshot: null), options: null, clock);
        var result = await verifier.TryDecodeAsync(token);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("state_signature_invalid");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-state-token")]
    [InlineData("a.b")]
    public async Task TryDecode_RejectsMalformedTokens(string raw)
    {
        var provider = new FakeOAuthClientProvider(Snapshot());
        var codec = new StateTokenCodec(provider, options: null, new FakeTimeProvider(DateTimeOffset.UtcNow));

        var result = await codec.TryDecodeAsync(raw);

        result.Succeeded.Should().BeFalse();
        result.Payload.Should().BeNull();
        result.ErrorCode.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TryDecode_AcceptsPreviousKidWithinGraceWindow()
    {
        // Issue #521 review v4-pro: HMAC key rotation must not invalidate
        // in-flight state tokens. State signed with the previous (demoted)
        // key still verifies for the configured StateTokenLifetime after
        // rotation; outside that window the previous key is rejected.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-29T10:00:00Z"));
        var oldKey = HmacKey;
        var newKey = Convert.FromHexString("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef".Replace("0", "9"));

        // Encode a state token with the OLD key + kid "v1".
        var oldSnapshot = Snapshot(hmacKey: oldKey, hmacKid: "v1");
        var oldCodec = new StateTokenCodec(new FakeOAuthClientProvider(oldSnapshot), options: null, clock);
        var token = await oldCodec.EncodeAsync("corr-1", SampleSubject(), "verifier-abc");

        // Rotate: new snapshot has v2 current and v1 demoted just now.
        var rotatedSnapshot = oldSnapshot with
        {
            HmacKid = "v2",
            HmacKey = newKey,
            HmacKeyRotatedAt = clock.GetUtcNow(),
            PreviousHmacKid = "v1",
            PreviousHmacKey = oldKey,
            PreviousHmacDemotedAt = clock.GetUtcNow(),
        };
        var rotatedCodec = new StateTokenCodec(new FakeOAuthClientProvider(rotatedSnapshot), options: null, clock);

        // Token from before the rotation must still verify (within grace).
        var withinGrace = await rotatedCodec.TryDecodeAsync(token);
        withinGrace.Succeeded.Should().BeTrue();

        // Advance past the lifetime → previous-key window closes, decode fails.
        clock.Advance(TimeSpan.FromMinutes(6));
        var afterGrace = await rotatedCodec.TryDecodeAsync(token);
        afterGrace.Succeeded.Should().BeFalse();
        // After-grace can either be expired (state_expired since lifetime passed)
        // or kid_unknown (key rejected); either is acceptable — both block.
        afterGrace.ErrorCode.Should().BeOneOf("state_expired", "state_kid_unknown");
    }

    private sealed class FakeOAuthClientProvider : IAevatarOAuthClientProvider
    {
        private readonly AevatarOAuthClientSnapshot? _snapshot;

        public FakeOAuthClientProvider(AevatarOAuthClientSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default)
        {
            if (_snapshot is null)
                throw new AevatarOAuthClientNotProvisionedException();
            return Task.FromResult(_snapshot);
        }
    }
}
