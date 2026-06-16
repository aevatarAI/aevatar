using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.VoicePresence.Abstractions;
using FluentAssertions;

namespace Aevatar.Bootstrap.Tests;

public sealed class VoiceToolCredentialIssuerTests
{
    [Fact]
    public async Task IssueAsync_ShouldMintOpaqueRefAndResolveTrimmedTokenUntilExpiry()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T10:00:00Z"));
        var issuer = new VoiceToolCredentialIssuer(timeProvider);
        var expiresAt = timeProvider.GetUtcNow().AddMinutes(5);

        var result = await issuer.IssueAsync(new VoiceToolCredentialIssueRequest(
            "  caller-token  ",
            expiresAt));

        result.Should().NotBeNull();
        result!.CredentialRef.Should().StartWith("voice-tool:");
        result.ExpiresAtUtc.Should().Be(expiresAt.ToUniversalTime());
        var resolved = await ((ICredentialProvider)issuer).ResolveAsync(result.CredentialRef);
        resolved.Should().Be("caller-token");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IssueAsync_ShouldRejectEmptyToken(string token)
    {
        var issuer = new VoiceToolCredentialIssuer();

        var result = await issuer.IssueAsync(new VoiceToolCredentialIssueRequest(
            token,
            DateTimeOffset.UtcNow.AddMinutes(5)));

        result.Should().BeNull();
    }

    [Fact]
    public async Task IssueAsync_ShouldRejectExpiredRequest()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T10:00:00Z"));
        var issuer = new VoiceToolCredentialIssuer(timeProvider);

        var result = await issuer.IssueAsync(new VoiceToolCredentialIssueRequest(
            "caller-token",
            timeProvider.GetUtcNow().AddSeconds(-1)));

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseAsync_ShouldEvictRef()
    {
        var issuer = new VoiceToolCredentialIssuer();
        var result = await issuer.IssueAsync(new VoiceToolCredentialIssueRequest(
            "caller-token",
            DateTimeOffset.UtcNow.AddMinutes(5)));

        await issuer.ReleaseAsync(result!.CredentialRef);

        var resolved = await ((ICredentialProvider)issuer).ResolveAsync(result.CredentialRef);
        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldEvictExpiredRef()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T10:00:00Z"));
        var issuer = new VoiceToolCredentialIssuer(timeProvider);
        var result = await issuer.IssueAsync(new VoiceToolCredentialIssueRequest(
            "caller-token",
            timeProvider.GetUtcNow().AddMinutes(5)));

        timeProvider.Advance(TimeSpan.FromMinutes(6));

        var resolved = await ((ICredentialProvider)issuer).ResolveAsync(result!.CredentialRef);
        resolved.Should().BeNull();
        var resolvedAgain = await ((ICredentialProvider)issuer).ResolveAsync(result.CredentialRef);
        resolvedAgain.Should().BeNull();
    }

    [Fact]
    public async Task IssueAsync_ShouldPropagateCancellation()
    {
        var issuer = new VoiceToolCredentialIssuer();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => issuer.IssueAsync(
            new VoiceToolCredentialIssueRequest("caller-token", DateTimeOffset.UtcNow.AddMinutes(5)),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow.ToUniversalTime();

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
