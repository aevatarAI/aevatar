using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.AI.Tests;

public sealed class NyxIdActionReadAuthorityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 12, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IssueAsync_SameRequestIdentity_ShouldReuseOpaqueReferenceWithoutExtendingExpiry()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new InMemorySecretVault(clock);
        var port = new NyxIdActionReadAuthorityPort(
            vault,
            clock,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromHours(24));

        var first = await port.IssueAsync(
            "bearer-original-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        clock.Advance(TimeSpan.FromMinutes(2));
        var replay = await port.IssueAsync(
            "bearer-refreshed-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        first.Succeeded.Should().BeTrue();
        replay.Succeeded.Should().BeTrue();
        replay.Authority.Should().BeEquivalentTo(first.Authority);
        replay.Authority!.SecretRef.Should().NotContain("bearer");
        replay.Authority.SecretRef.Should().NotContain("owner-alpha");
        replay.Authority.SecretRef.Should().NotContain("scope-alpha");
        replay.Authority.ExpiresAtUnixMs.Should().Be(
            Now.AddMinutes(10).ToUnixTimeMilliseconds());

        var resolved = await port.ResolveAsync(
            replay.Authority,
            "scope-alpha",
            "owner-alpha");

        resolved.Resolved.Should().BeTrue();
        ResolveBearerToken(resolved).Should().Be("bearer-original-alpha",
            "an idempotent replay cannot replace the authority accepted by the first request");
    }

    [Fact]
    public async Task IssueAsync_DifferentRequestIdentity_ShouldIssueDifferentAuthority()
    {
        var clock = new FakeTimeProvider(Now);
        var port = CreatePort(new InMemorySecretVault(clock), clock);

        var first = await port.IssueAsync(
            "bearer-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        var second = await port.IssueAsync(
            "bearer-beta",
            "scope-alpha",
            "owner-alpha",
            "command-action-beta");

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        second.Authority!.SecretRef.Should().NotBe(first.Authority!.SecretRef);
    }

    [Fact]
    public async Task IssueAsync_ExpiredRequestWithDurableFence_ShouldNotReissueAuthority()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new PurgeableActionAuthorityVault(clock);
        var port = CreatePort(vault, clock);
        var first = await port.IssueAsync(
            "bearer-original-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        clock.Advance(TimeSpan.FromMinutes(11));
        vault.PurgeAuthorityRecords();
        var restartedPort = CreatePort(vault, clock);
        var replay = await restartedPort.IssueAsync(
            "bearer-refreshed-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        var freshWake = await restartedPort.IssueAsync(
            "bearer-refreshed-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-beta");

        first.Succeeded.Should().BeTrue();
        replay.Succeeded.Should().BeFalse();
        replay.FailureCode.Should().Be(NyxIdActionReadAuthorityPort.ExpiredCode);
        freshWake.Succeeded.Should().BeTrue(
            "a newly authenticated state-change wake has a new client request identity");
    }

    [Fact]
    public async Task ResolveAsync_NewPortInstance_ShouldResolveUnexpiredAuthority()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new InMemorySecretVault(clock);
        var issued = await CreatePort(vault, clock).IssueAsync(
            "bearer-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        var resolved = await CreatePort(vault, clock).ResolveAsync(
            issued.Authority,
            "scope-alpha",
            "owner-alpha");

        resolved.Resolved.Should().BeTrue();
        ResolveBearerToken(resolved).Should().Be("bearer-alpha");
    }

    [Fact]
    public async Task ResolveAsync_UnexpiredReferenceWithMissingVaultRecord_ShouldReturnMissing()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new PurgeableActionAuthorityVault(clock);
        var port = CreatePort(vault, clock);
        var issued = await port.IssueAsync(
            "bearer-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        vault.PurgeAuthorityRecords();
        var resolved = await port.ResolveAsync(
            issued.Authority,
            "scope-alpha",
            "owner-alpha");

        resolved.Resolved.Should().BeFalse();
        resolved.FailureCode.Should().Be(NyxIdActionReadAuthorityPort.MissingCode);
    }

    [Fact]
    public async Task IssueAsync_ShouldPreserveBearerWithoutNormalization()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new InMemorySecretVault(clock);
        var port = CreatePort(vault, clock);
        var bearer = " bearer-alpha ";

        var issued = await port.IssueAsync(
            bearer,
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        var stored = await vault.ResolveAsync(new ResolveSecretRequest(
            issued.Authority!.SecretRef,
            CredentialSecretPurposes.NyxIdChatActionReadAuthority,
            "scope-alpha",
            "owner-alpha",
            "test-resolve"));
        var resolved = await port.ResolveAsync(issued.Authority, "scope-alpha", "owner-alpha");

        stored.Secret.Should().Be(bearer);
        resolved.ToString().Should().NotContain(bearer);
    }

    [Fact]
    public async Task ResolveAsync_InvalidBindings_ShouldFailClosedWithStableCodes()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new InMemorySecretVault(clock);
        var port = CreatePort(vault, clock);
        var issued = await port.IssueAsync(
            "bearer-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        var authority = issued.Authority!;

        (await port.ResolveAsync(null, "scope-alpha", "owner-alpha"))
            .FailureCode.Should().Be(NyxIdActionReadAuthorityPort.MissingCode);
        (await port.ResolveAsync(authority, "scope-other", "owner-alpha"))
            .FailureCode.Should().Be(NyxIdActionReadAuthorityPort.ScopeMismatchCode);
        (await port.ResolveAsync(authority, "scope-alpha", "owner-other"))
            .FailureCode.Should().Be(NyxIdActionReadAuthorityPort.OwnerMismatchCode);

        var wrongPurpose = authority.Clone();
        wrongPurpose.Purpose = "wrong-purpose";
        (await port.ResolveAsync(wrongPurpose, "scope-alpha", "owner-alpha"))
            .FailureCode.Should().Be(NyxIdActionReadAuthorityPort.PurposeMismatchCode);

        clock.Advance(TimeSpan.FromMinutes(11));
        (await port.ResolveAsync(authority, "scope-alpha", "owner-alpha"))
            .FailureCode.Should().Be(NyxIdActionReadAuthorityPort.ExpiredCode);
    }

    [Fact]
    public async Task RevokeAsync_ShouldMakeAuthorityUnresolvable()
    {
        var clock = new FakeTimeProvider(Now);
        var port = CreatePort(new InMemorySecretVault(clock), clock);
        var issued = await port.IssueAsync(
            "bearer-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");

        var revoked = await port.RevokeAsync(
            issued.Authority,
            "scope-alpha",
            "owner-alpha");
        var resolved = await port.ResolveAsync(
            issued.Authority,
            "scope-alpha",
            "owner-alpha");

        revoked.Should().BeTrue();
        resolved.Resolved.Should().BeFalse();
        resolved.FailureCode.Should().Be(NyxIdActionReadAuthorityPort.RevokedCode);
    }

    private static NyxIdActionReadAuthorityPort CreatePort(
        ISecretVault vault,
        TimeProvider clock) =>
        new(
            vault,
            clock,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromHours(24));

    private static string? ResolveBearerToken(
        NyxIdActionReadAuthorityResolution resolution)
    {
        var context = AgentToolExecutionContextMapper.FromPayload(
            resolution.CloneTransientToolContext());
        return AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(context.Credentials);
    }

    private sealed class PurgeableActionAuthorityVault : ISecretVault
    {
        private readonly TimeProvider _clock;
        private ISecretVault _authorityVault;
        private readonly ISecretVault _otherVault;

        public PurgeableActionAuthorityVault(TimeProvider clock)
        {
            _clock = clock;
            _authorityVault = new InMemorySecretVault(clock);
            _otherVault = new InMemorySecretVault(clock);
        }

        public void PurgeAuthorityRecords() =>
            _authorityVault = new InMemorySecretVault(_clock);

        public Task<StoreSecretResult> PutAsync(
            StoreSecretRequest request,
            CancellationToken ct = default) =>
            Select(request.Purpose).PutAsync(request, ct);

        public Task<ResolveSecretResult> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken ct = default) =>
            Select(request.Purpose).ResolveAsync(request, ct);

        public Task<RotateSecretResult> RotateAsync(
            RotateSecretRequest request,
            CancellationToken ct = default) =>
            Select(request.Purpose).RotateAsync(request, ct);

        public Task<RevokeSecretResult> RevokeAsync(
            RevokeSecretRequest request,
            CancellationToken ct = default) =>
            Select(request.Purpose).RevokeAsync(request, ct);

        private ISecretVault Select(string purpose) =>
            string.Equals(
                purpose,
                CredentialSecretPurposes.NyxIdChatActionReadAuthority,
                StringComparison.Ordinal)
                ? _authorityVault
                : _otherVault;
    }
}
