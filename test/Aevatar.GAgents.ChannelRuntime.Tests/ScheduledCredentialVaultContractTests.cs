using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ScheduledCredentialVaultContractTests
{
    [Fact]
    public async Task PutAsync_WithRequestedReference_IsIdempotentForExactCreate()
    {
        var vault = new Aevatar.Foundation.Abstractions.Credentials.Testing.InMemorySecretVault();
        var request = new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-scope",
            "key-a",
            "opaque-secret",
            "test",
            RequestedRef: "sec_requested_a");

        var first = await vault.PutAsync(request);
        var second = await vault.PutAsync(request);

        first.Reference.Ref.Should().Be("sec_requested_a");
        second.Reference.Should().BeEquivalentTo(first.Reference);
    }

    [Fact]
    public async Task PutAsync_WithRequestedReference_RejectsAliasConflict()
    {
        var vault = new Aevatar.Foundation.Abstractions.Credentials.Testing.InMemorySecretVault();
        await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-scope",
            "key-a",
            "opaque-secret",
            "test",
            RequestedRef: "sec_requested_a"));

        Func<Task> act = () => vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-scope",
            "key-b",
            "different-secret",
            "test",
            RequestedRef: "sec_requested_a"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ProvisionAsync_WhenIssueFails_DoesNotWriteVaultOrRevocationIntent()
    {
        var vault = Substitute.For<ISecretVault>();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var issuer = Substitute.For<IScheduledAgentApiKeyIssuer>();
        issuer.IssueAsync(
                Arg.Any<string>(),
                Arg.Any<ScheduledAgentServiceSlugs>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(ScheduledAgentApiKeyIssueResult.Failed("issue_failed"));
        var lifecycle = new ScheduledAgentCredentialLifecycle(vault, commandPort, issuer);

        var result = await lifecycle.ProvisionAsync(
            "token",
            new ScheduledAgentServiceSlugs("service", null, [], false),
            "agent-a",
            "skill-a",
            "scope-a",
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-a",
            "test");

        result.Success.Should().BeFalse();
        await vault.DidNotReceive().PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>());
        await commandPort.DidNotReceive().RequestCredentialRevocationAsync(
            Arg.Any<UserAgentApiKeyRevocation>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionAsync_WhenVaultWriteFails_CommitsDualTrackRevocationIntent()
    {
        var vault = Substitute.For<ISecretVault>();
        vault.PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<StoreSecretResult>>(_ => throw new InvalidOperationException("vault unavailable"));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var issuer = Substitute.For<IScheduledAgentApiKeyIssuer>();
        issuer.IssueAsync(
                Arg.Any<string>(),
                Arg.Any<ScheduledAgentServiceSlugs>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(ScheduledAgentApiKeyIssueResult.Succeeded("key-a", "raw-secret"));
        var lifecycle = new ScheduledAgentCredentialLifecycle(vault, commandPort, issuer);

        Func<Task> act = () => lifecycle.ProvisionAsync(
            "token",
            new ScheduledAgentServiceSlugs("service", null, [], false),
            "agent-a",
            "skill-a",
            "scope-a",
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-a",
            "test");

        await act.Should().ThrowAsync<InvalidOperationException>();
        await commandPort.Received(1).RequestCredentialRevocationAsync(
            Arg.Is<UserAgentApiKeyRevocation>(intent =>
                intent.AgentId == "agent-a" &&
                intent.ApiKeyId == "key-a" &&
                intent.NyxApiKeyReference.Ref.StartsWith("sec_", StringComparison.Ordinal) &&
                intent.NyxApiKeyReference.OwnerScopeKey == "owner-a" &&
                intent.NyxIdTrack.Status == ScheduledCredentialRevocationTrackStatus.Pending &&
                intent.VaultTrack.Status == ScheduledCredentialRevocationTrackStatus.Pending),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionAsync_WhenSuccessful_ReturnsDurableRequestedReference()
    {
        var vault = new Aevatar.Foundation.Abstractions.Credentials.Testing.InMemorySecretVault();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var issuer = Substitute.For<IScheduledAgentApiKeyIssuer>();
        issuer.IssueAsync(
                Arg.Any<string>(),
                Arg.Any<ScheduledAgentServiceSlugs>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(ScheduledAgentApiKeyIssueResult.Succeeded("key-a", "raw-secret"));
        var lifecycle = new ScheduledAgentCredentialLifecycle(vault, commandPort, issuer);

        var result = await lifecycle.ProvisionAsync(
            "token",
            new ScheduledAgentServiceSlugs("service", null, [], false),
            "agent-a",
            "skill-a",
            "scope-a",
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-a",
            "test");

        result.Success.Should().BeTrue();
        result.SecretReference!.Ref.Should().StartWith("sec_");
        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            result.SecretReference.Ref,
            result.SecretReference.Purpose,
            result.SecretReference.OwnerScopeKey,
            "key-a",
            "test"));
        resolved.Secret.Should().Be("raw-secret");
    }
}
