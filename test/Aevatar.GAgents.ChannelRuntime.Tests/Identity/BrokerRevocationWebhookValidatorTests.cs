using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Pins HMAC verification on inbound NyxID revocation webhooks. Notable
/// case (PR #521 review kimi MAJOR security): when the cluster has just
/// rotated its HMAC key, NyxID may still sign in-flight webhooks with the
/// previous key — silently rejecting them would drop real CAE
/// revocations. The validator must accept the previous key for the same
/// rotation grace window the state-token codec uses.
/// </summary>
public sealed class BrokerRevocationWebhookValidatorTests
{
    private static readonly byte[] CurrentKey =
        Convert.FromHexString("11111111111111111111111111111111111111111111111111111111111111aa");

    private static readonly byte[] PreviousKey =
        Convert.FromHexString("22222222222222222222222222222222222222222222222222222222222222bb");

    // Property names match BrokerRevocationNotificationDto's PascalCase since
    // the validator's JsonSerializer.Deserialize call uses default (case-
    // sensitive) options. NyxID payload-shape compatibility is a separate
    // concern outside this PR.
    private static readonly byte[] WebhookBody =
        Encoding.UTF8.GetBytes("""{"EventType":"binding_revoked","BindingId":"bnd_1","Platform":"lark","Tenant":"t","ExternalUserId":"u"}""");

    [Fact]
    public async Task Accepts_CurrentKey_HappyPath()
    {
        var snapshot = NewSnapshot(currentKey: CurrentKey);
        var validator = NewValidator(snapshot);
        var http = NewHttpContext(SignBody(CurrentKey));

        var result = await validator.ValidateAsync(http, WebhookBody);

        result.Succeeded.Should().BeTrue();
        result.Notification.Should().NotBeNull();
        result.Notification!.BindingId.Should().Be("bnd_1");
    }

    [Fact]
    public async Task RejectsForgedSignature()
    {
        var snapshot = NewSnapshot(currentKey: CurrentKey);
        var validator = NewValidator(snapshot);
        var http = NewHttpContext(SignBody(PreviousKey)); // wrong key, no previous slot

        var result = await validator.ValidateAsync(http, WebhookBody);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("signature_mismatch");
    }

    [Fact]
    public async Task AcceptsPreviousKey_WithinGraceWindow()
    {
        // Just rotated: NyxID's outbound webhook still carries a signature
        // produced with the demoted key. The validator must accept it for
        // the configured StateTokenLifetime grace window.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-30T10:00:00Z"));
        var snapshot = NewSnapshot(
            currentKey: CurrentKey,
            previousKey: PreviousKey,
            previousDemotedAt: clock.GetUtcNow().AddSeconds(-30));
        var validator = NewValidator(snapshot, clock);
        var http = NewHttpContext(SignBody(PreviousKey));

        var result = await validator.ValidateAsync(http, WebhookBody);

        result.Succeeded.Should().BeTrue();
        result.Notification.Should().NotBeNull();
    }

    [Fact]
    public async Task RejectsPreviousKey_AfterGraceWindow()
    {
        // Same setup but the rotation happened 6 minutes ago, beyond the
        // 5-minute default state-token lifetime — the previous key must
        // no longer verify.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-30T10:00:00Z"));
        var snapshot = NewSnapshot(
            currentKey: CurrentKey,
            previousKey: PreviousKey,
            previousDemotedAt: clock.GetUtcNow().AddMinutes(-6));
        var validator = NewValidator(snapshot, clock);
        var http = NewHttpContext(SignBody(PreviousKey));

        var result = await validator.ValidateAsync(http, WebhookBody);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("signature_mismatch");
    }

    [Fact]
    public async Task RejectsMissingSignatureHeader()
    {
        var snapshot = NewSnapshot(currentKey: CurrentKey);
        var validator = NewValidator(snapshot);
        var http = new DefaultHttpContext();

        var result = await validator.ValidateAsync(http, WebhookBody);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("signature_missing");
    }

    [Fact]
    public async Task RejectsMalformedSignatureScheme()
    {
        var snapshot = NewSnapshot(currentKey: CurrentKey);
        var validator = NewValidator(snapshot);
        var http = NewHttpContext("md5=abcdef");

        var result = await validator.ValidateAsync(http, WebhookBody);

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("signature_scheme_unsupported");
    }

    private static AevatarOAuthClientSnapshot NewSnapshot(
        byte[] currentKey,
        byte[]? previousKey = null,
        DateTimeOffset? previousDemotedAt = null) => new(
        ClientId: "aevatar-channel-binding",
        ClientIdIssuedAt: DateTimeOffset.Parse("2026-04-30T09:00:00Z"),
        HmacKid: "v2",
        HmacKey: currentKey,
        HmacKeyRotatedAt: DateTimeOffset.Parse("2026-04-30T09:30:00Z"),
        NyxIdAuthority: "https://nyxid.test",
        BrokerCapabilityObserved: true,
        BrokerCapabilityObservedAt: DateTimeOffset.Parse("2026-04-30T09:00:00Z"),
        PreviousHmacKid: previousKey is null ? null : "v1",
        PreviousHmacKey: previousKey,
        PreviousHmacDemotedAt: previousDemotedAt);

    private static BrokerRevocationWebhookValidator NewValidator(
        AevatarOAuthClientSnapshot snapshot,
        FakeTimeProvider? clock = null)
    {
        var provider = new FakeOAuthClientProvider(snapshot);
        var options = Options.Create(new NyxIdBrokerOptions { StateTokenLifetime = TimeSpan.FromMinutes(5) });
        return new BrokerRevocationWebhookValidator(provider, options, clock);
    }

    private static HttpContext NewHttpContext(string signatureHeader)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers[BrokerRevocationWebhookValidator.SignatureHeader] = signatureHeader;
        return http;
    }

    private static string SignBody(byte[] key)
    {
        var hmac = HMACSHA256.HashData(key, WebhookBody);
        return $"sha256={Convert.ToHexString(hmac).ToLowerInvariant()}";
    }

    private sealed class FakeOAuthClientProvider : IAevatarOAuthClientProvider
    {
        private readonly AevatarOAuthClientSnapshot _snapshot;
        public FakeOAuthClientProvider(AevatarOAuthClientSnapshot snapshot) => _snapshot = snapshot;
        public Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(_snapshot);
    }
}
