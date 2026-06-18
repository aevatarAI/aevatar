using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Hosting;
using FluentAssertions;

namespace Aevatar.Bootstrap.Tests;

public sealed class VoiceVolatileToolCredentialPortTests
{
    [Fact]
    public async Task IssueAsync_ShouldMintOpaqueRefAndResolveOnlyAfterTransportLeaseBinding()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T10:00:00Z"));
        var issuer = new VoiceVolatileToolCredentialPort(timeProvider);
        var expiresAt = timeProvider.GetUtcNow().AddMinutes(5);

        var result = await issuer.IssueAsync(new VoiceToolCredentialIssueRequest(
            "  caller-token  ",
            expiresAt));

        result.Should().NotBeNull();
        result!.CredentialRef.Should().StartWith("voice-tool:");
        result.ExpiresAtUtc.Should().Be(expiresAt.ToUniversalTime());
        result.TransportBinding.Should().NotBeNull();
        result.TransportBinding!.CredentialRef.Should().Be(result.CredentialRef);
        result.TransportBinding.NyxIdAccessToken.Should().Be("caller-token");
        var unresolved = await ((ICredentialProvider)issuer).ResolveAsync(result.CredentialRef);
        unresolved.Should().BeNull();

        (await issuer.BindTransportLeaseAsync(result.TransportBinding, "transport-1")).Should().BeTrue();
        var resolved = await ((ICredentialProvider)issuer).ResolveAsync(result.CredentialRef);
        resolved.Should().Be("caller-token");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IssueAsync_ShouldRejectEmptyToken(string token)
    {
        var issuer = new VoiceVolatileToolCredentialPort();

        var result = await issuer.IssueAsync(new VoiceToolCredentialIssueRequest(
            token,
            DateTimeOffset.UtcNow.AddMinutes(5)));

        result.Should().BeNull();
    }

    [Fact]
    public async Task IssueAsync_ShouldRejectExpiredRequest()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T10:00:00Z"));
        var issuer = new VoiceVolatileToolCredentialPort(timeProvider);

        var result = await issuer.IssueAsync(new VoiceToolCredentialIssueRequest(
            "caller-token",
            timeProvider.GetUtcNow().AddSeconds(-1)));

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseAsync_ShouldEvictRef()
    {
        var issuer = new VoiceVolatileToolCredentialPort();
        var result = await issuer.IssueAsync(new VoiceToolCredentialIssueRequest(
            "caller-token",
            DateTimeOffset.UtcNow.AddMinutes(5)));

        await issuer.BindTransportLeaseAsync(result!.TransportBinding!, "transport-1");
        await issuer.ReleaseAsync(result.CredentialRef);

        var resolved = await ((ICredentialProvider)issuer).ResolveAsync(result.CredentialRef);
        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseTransportLeaseAsync_ShouldEvictRef()
    {
        var issuer = new VoiceVolatileToolCredentialPort();
        var result = await issuer.IssueAsync(new VoiceToolCredentialIssueRequest(
            "caller-token",
            DateTimeOffset.UtcNow.AddMinutes(5)));

        await issuer.BindTransportLeaseAsync(result!.TransportBinding!, "transport-1");
        await issuer.ReleaseTransportLeaseAsync("transport-1");

        var resolved = await ((ICredentialProvider)issuer).ResolveAsync(result.CredentialRef);
        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldEvictExpiredTransportLeaseRef()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-16T10:00:00Z"));
        var issuer = new VoiceVolatileToolCredentialPort(timeProvider);
        var result = await issuer.IssueAsync(new VoiceToolCredentialIssueRequest(
            "caller-token",
            timeProvider.GetUtcNow().AddMinutes(5)));

        await issuer.BindTransportLeaseAsync(result!.TransportBinding!, "transport-1");
        timeProvider.Advance(TimeSpan.FromMinutes(6));

        var resolved = await ((ICredentialProvider)issuer).ResolveAsync(result.CredentialRef);
        resolved.Should().BeNull();
        var resolvedAgain = await ((ICredentialProvider)issuer).ResolveAsync(result.CredentialRef);
        resolvedAgain.Should().BeNull();
    }

    [Fact]
    public async Task IssueAsync_ShouldPropagateCancellation()
    {
        var issuer = new VoiceVolatileToolCredentialPort();
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
