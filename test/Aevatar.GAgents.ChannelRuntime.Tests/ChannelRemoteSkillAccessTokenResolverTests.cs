using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRemoteSkillAccessTokenResolverTests
{
    [Fact]
    public async Task ResolveAsync_WithoutSenderBinding_UsesTypedSourceReadableCallerToken()
    {
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        var resolver = NewResolver(issuer);
        using var context = PushContext(
            bindingId: null,
            senderToken: null,
            ownerToken: "ambient-owner-token",
            credentialKind: AgentToolNyxIdCredentialKind.SourceReadableUserBearer);

        var resolution = await resolver.ResolveAsync("nyxid");

        resolution.Succeeded.Should().BeTrue();
        resolution.AccessToken.Should().Be("ambient-owner-token");
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task ResolveAsync_WithoutSenderBinding_UsesSupplementalSourceCredentialInsteadOfDelegation()
    {
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        var resolver = NewResolver(issuer);
        using var context = PushContext(
            bindingId: null,
            senderToken: null,
            ownerToken: "delegation-token",
            credentialKind: AgentToolNyxIdCredentialKind.ProxyDelegation,
            sourceReadableToken: "source-readable-token");

        var resolution = await resolver.ResolveAsync("nyxid");

        resolution.Succeeded.Should().BeTrue();
        resolution.AccessToken.Should().Be("source-readable-token");
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task ResolveAsync_WithoutSenderBinding_DoesNotUseDelegationAsSourceCredential()
    {
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        var resolver = NewResolver(issuer);
        using var context = PushContext(
            bindingId: null,
            senderToken: null,
            ownerToken: "delegation-token",
            credentialKind: AgentToolNyxIdCredentialKind.ProxyDelegation);

        var resolution = await resolver.ResolveAsync("nyxid");

        resolution.Succeeded.Should().BeFalse();
        resolution.FailureKind.Should().Be(RemoteSkillAccessTokenFailureKind.ChannelBindingRequired);
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task ResolveAsync_ForUnboundDefaultSkillBinding_UsesChannelRegistrationAgentKey()
    {
        var secretVault = new InMemorySecretVault();
        var stored = await secretVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            "scope-channel-alpha",
            "agent-key-channel-alpha",
            "channel-agent-key-token",
            "test"));
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        var resolver = NewResolver(issuer, secretVault);
        using var context = PushContext(
            bindingId: null,
            senderToken: null,
            ownerToken: null,
            channelCredential: new ChannelWorkflowResultDeliveryCredential
            {
                SecretReference = stored.Reference.Clone(),
                SubjectId = "agent-key-channel-alpha",
            },
            executionOwner: AgentToolExecutionOwners.ChannelRegistration("reg-channel-alpha"),
            botRegistrationId: "reg-channel-alpha",
            skillRecovery: new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: true,
                RequireOrnnSearchOnBlocker: true,
                CommandName: "test-default-skill",
                OriginalCommand: "default-route-proof-1",
                PrimarySkillName: "test-default-skill",
                MaxOrnnSearchAttempts: 2,
                CommandArguments: "default-route-proof-1",
                DiscoveryRequested: false,
                IsolatePriorConversationHistory: false,
                MountWorkflowsRequested: false,
                FromChannelDefaultSkillBinding: true));

        var resolution = await resolver.ResolveAsync("test-default-skill");

        resolution.Succeeded.Should().BeTrue();
        resolution.AccessToken.Should().Be("channel-agent-key-token");
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task ResolveAsync_ForBoundDefaultSkillBinding_PrefersChannelRegistrationAgentKeyOverSenderToken()
    {
        var secretVault = new InMemorySecretVault();
        var stored = await secretVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            "scope-channel-alpha",
            "agent-key-channel-alpha",
            "channel-agent-key-token",
            "test"));
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        var resolver = NewResolver(issuer, secretVault);
        using var context = PushContext(
            bindingId: "bnd-other-user",
            senderToken: "sender-token-that-cannot-read-private-skill",
            ownerToken: null,
            channelCredential: new ChannelWorkflowResultDeliveryCredential
            {
                SecretReference = stored.Reference.Clone(),
                SubjectId = "agent-key-channel-alpha",
            },
            executionOwner: AgentToolExecutionOwners.ChannelRegistration("reg-channel-alpha"),
            botRegistrationId: "reg-channel-alpha",
            skillRecovery: new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: true,
                RequireOrnnSearchOnBlocker: true,
                CommandName: "test-default-skill",
                OriginalCommand: "default-route-proof-1",
                PrimarySkillName: "test-default-skill",
                MaxOrnnSearchAttempts: 2,
                CommandArguments: "default-route-proof-1",
                DiscoveryRequested: false,
                IsolatePriorConversationHistory: false,
                MountWorkflowsRequested: false,
                FromChannelDefaultSkillBinding: true));

        var resolution = await resolver.ResolveAsync("test-default-skill");

        resolution.Succeeded.Should().BeTrue();
        resolution.AccessToken.Should().Be("channel-agent-key-token");
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task ResolveAsync_ForExplicitSkillTrigger_DoesNotUseChannelRegistrationAgentKey()
    {
        var secretVault = new InMemorySecretVault();
        var stored = await secretVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            "scope-channel-alpha",
            "agent-key-channel-alpha",
            "channel-agent-key-token",
            "test"));
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        var resolver = NewResolver(issuer, secretVault);
        using var context = PushContext(
            bindingId: null,
            senderToken: null,
            ownerToken: null,
            channelCredential: new ChannelWorkflowResultDeliveryCredential
            {
                SecretReference = stored.Reference.Clone(),
                SubjectId = "agent-key-channel-alpha",
            },
            executionOwner: AgentToolExecutionOwners.ChannelRegistration("reg-channel-alpha"),
            botRegistrationId: "reg-channel-alpha",
            skillRecovery: new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: true,
                RequireOrnnSearchOnBlocker: true,
                CommandName: "other-private-skill",
                OriginalCommand: "/other-private-skill attempt",
                PrimarySkillName: "other-private-skill",
                MaxOrnnSearchAttempts: 2,
                CommandArguments: "attempt",
                DiscoveryRequested: false,
                IsolatePriorConversationHistory: true,
                MountWorkflowsRequested: false,
                FromChannelDefaultSkillBinding: false));

        var resolution = await resolver.ResolveAsync("other-private-skill");

        resolution.Succeeded.Should().BeFalse();
        resolution.FailureKind.Should().Be(RemoteSkillAccessTokenFailureKind.ChannelBindingRequired);
    }

    [Fact]
    public async Task ResolveAsync_ForDefaultSkillBindingWithMismatchedSkill_DoesNotUseChannelRegistrationAgentKey()
    {
        var secretVault = new InMemorySecretVault();
        var stored = await secretVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            "scope-channel-alpha",
            "agent-key-channel-alpha",
            "channel-agent-key-token",
            "test"));
        var resolver = NewResolver(null, secretVault);
        using var context = PushContext(
            bindingId: null,
            senderToken: null,
            ownerToken: null,
            channelCredential: new ChannelWorkflowResultDeliveryCredential
            {
                SecretReference = stored.Reference.Clone(),
                SubjectId = "agent-key-channel-alpha",
            },
            executionOwner: AgentToolExecutionOwners.ChannelRegistration("reg-channel-alpha"),
            botRegistrationId: "reg-channel-alpha",
            skillRecovery: new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: true,
                RequireOrnnSearchOnBlocker: true,
                CommandName: "test-default-skill",
                OriginalCommand: "default-route-proof-1",
                PrimarySkillName: "test-default-skill",
                MaxOrnnSearchAttempts: 2,
                CommandArguments: "default-route-proof-1",
                DiscoveryRequested: false,
                IsolatePriorConversationHistory: false,
                MountWorkflowsRequested: false,
                FromChannelDefaultSkillBinding: true));

        var resolution = await resolver.ResolveAsync("other-private-skill");

        resolution.Succeeded.Should().BeFalse();
        resolution.FailureKind.Should().Be(RemoteSkillAccessTokenFailureKind.ChannelBindingRequired);
    }

    [Fact]
    public async Task ResolveAsync_WithVerifiedSenderToken_ReusesItWithoutIssuingCapability()
    {
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        var resolver = NewResolver(issuer);
        using var context = PushContext(
            bindingId: "bnd-skill-alpha",
            senderToken: " sender-route-token ",
            ownerToken: "ambient-owner-token");

        var resolution = await resolver.ResolveAsync("nyxid");

        resolution.Succeeded.Should().BeTrue();
        resolution.AccessToken.Should().Be("sender-route-token");
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task ResolveAsync_WithBindingAndTypedAuthority_IssuesForExactSubject()
    {
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                "bnd-skill-alpha",
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CapabilityHandle
            {
                AccessToken = "sender-skill-token",
                Scope = "proxy",
            }));
        var resolver = NewResolver(issuer);
        using var context = PushContext(
            bindingId: "bnd-skill-alpha",
            senderToken: null,
            ownerToken: "ambient-owner-token",
            authority: new AgentToolNyxIdAuthorityContext(
                "lark",
                "tenant-authority-alpha",
                "ou-authority-alpha"));

        var resolution = await resolver.ResolveAsync("nyxid");

        resolution.Succeeded.Should().BeTrue();
        resolution.AccessToken.Should().Be("sender-skill-token");
        await issuer.Received(1).IssueByBindingIdAsync(
            Arg.Is<ExternalSubjectRef>(subject =>
                subject.Platform == "lark" &&
                subject.Tenant == "tenant-authority-alpha" &&
                subject.ExternalUserId == "ou-authority-alpha"),
            "bnd-skill-alpha",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_WithBindingButIncompleteAuthority_DoesNotUseAmbientOwnerToken()
    {
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        var resolver = NewResolver(issuer);
        using var context = PushContext(
            bindingId: "bnd-skill-alpha",
            senderToken: null,
            ownerToken: "ambient-owner-token",
            authority: AgentToolNyxIdAuthorityContext.Empty);

        var resolution = await resolver.ResolveAsync("nyxid");

        resolution.Succeeded.Should().BeFalse();
        resolution.FailureKind.Should().Be(RemoteSkillAccessTokenFailureKind.Unavailable);
        await issuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task ResolveAsync_WhenBoundCapabilityIssueFails_DoesNotUseAmbientOwnerToken()
    {
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new HttpRequestException("NyxID unavailable"));
        var resolver = NewResolver(issuer);
        using var context = PushContext(
            bindingId: "bnd-skill-alpha",
            senderToken: null,
            ownerToken: "ambient-owner-token",
            authority: CompleteAuthority());

        var resolution = await resolver.ResolveAsync("nyxid");

        resolution.Succeeded.Should().BeFalse();
        resolution.FailureKind.Should().Be(RemoteSkillAccessTokenFailureKind.Unavailable);
    }

    [Fact]
    public async Task ResolveAsync_WhenBoundCapabilityIssuerIsUnavailable_DoesNotUseAmbientOwnerToken()
    {
        var resolver = NewResolver(capabilityIssuer: null);
        using var context = PushContext(
            bindingId: "bnd-skill-alpha",
            senderToken: null,
            ownerToken: "ambient-owner-token",
            authority: CompleteAuthority());

        var resolution = await resolver.ResolveAsync("nyxid");

        resolution.Succeeded.Should().BeFalse();
        resolution.FailureKind.Should().Be(RemoteSkillAccessTokenFailureKind.Unavailable);
    }

    [Fact]
    public async Task ResolveAsync_WhenBindingIsRevoked_ReportsChannelBindingRefreshRequired()
    {
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new BindingRevokedException(CompleteSubject()));
        var resolver = NewResolver(issuer);
        using var context = PushContext(
            bindingId: "bnd-skill-alpha",
            senderToken: null,
            ownerToken: "ambient-owner-token",
            authority: CompleteAuthority());

        var resolution = await resolver.ResolveAsync("nyxid");

        resolution.Succeeded.Should().BeFalse();
        resolution.FailureKind.Should().Be(RemoteSkillAccessTokenFailureKind.ChannelBindingRefreshRequired);
        resolution.AccessToken.Should().BeNull("a revoked binding must never fall back to ambient owner credentials");
    }

    [Fact]
    public async Task ResolveAsync_WhenBindingScopeMismatch_ReportsChannelBindingRefreshRequired()
    {
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new BindingScopeMismatchException(CompleteSubject()));
        var resolver = NewResolver(issuer);
        using var context = PushContext(
            bindingId: "bnd-skill-alpha",
            senderToken: null,
            ownerToken: "ambient-owner-token",
            authority: CompleteAuthority());

        var resolution = await resolver.ResolveAsync("nyxid");

        resolution.Succeeded.Should().BeFalse();
        resolution.FailureKind.Should().Be(RemoteSkillAccessTokenFailureKind.ChannelBindingRefreshRequired);
    }

    [Fact]
    public async Task ResolveAsync_WhenBindingServiceAccessMismatch_ReportsChannelBindingRefreshRequired()
    {
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new BindingServiceAccessMismatchException(
                CompleteSubject(),
                new[] { "urn:nyxid:service:llm" }));
        var resolver = NewResolver(issuer);
        using var context = PushContext(
            bindingId: "bnd-skill-alpha",
            senderToken: null,
            ownerToken: "ambient-owner-token",
            authority: CompleteAuthority());

        var resolution = await resolver.ResolveAsync("nyxid");

        resolution.Succeeded.Should().BeFalse();
        resolution.FailureKind.Should().Be(RemoteSkillAccessTokenFailureKind.ChannelBindingRefreshRequired);
    }

    [Fact]
    public async Task ResolveAsync_WhenIssuedCapabilityLacksToken_ReportsUnavailable()
    {
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CapabilityHandle
            {
                AccessToken = "",
                Scope = "proxy",
            }));
        var resolver = NewResolver(issuer);
        using var context = PushContext(
            bindingId: "bnd-skill-alpha",
            senderToken: null,
            ownerToken: "ambient-owner-token",
            authority: CompleteAuthority());

        var resolution = await resolver.ResolveAsync("nyxid");

        resolution.Succeeded.Should().BeFalse();
        resolution.FailureKind.Should().Be(RemoteSkillAccessTokenFailureKind.Unavailable);
    }

    [Fact]
    public async Task ResolveAsync_WhenCapabilityIssueIsCanceled_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var issuer = Substitute.For<INyxIdSkillCapabilityIssuer>();
        issuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                cts.Token)
            .Returns(Task.FromCanceled<CapabilityHandle>(cts.Token));
        var resolver = NewResolver(issuer);
        using var context = PushContext(
            bindingId: "bnd-skill-alpha",
            senderToken: null,
            ownerToken: "ambient-owner-token",
            authority: CompleteAuthority());

        var act = () => resolver.ResolveAsync("nyxid", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ChannelRemoteSkillAccessTokenResolver NewResolver(
        INyxIdSkillCapabilityIssuer? capabilityIssuer,
        ISecretVault? secretVault = null) =>
        new(
            capabilityIssuer,
            secretVault,
            NullLogger<ChannelRemoteSkillAccessTokenResolver>.Instance);

    private static AgentToolContextScope PushContext(
        string? bindingId,
        string? senderToken,
        string? ownerToken,
        AgentToolNyxIdAuthorityContext? authority = null,
        AgentToolNyxIdCredentialKind credentialKind = AgentToolNyxIdCredentialKind.Unspecified,
        string? sourceReadableToken = null,
        ChannelWorkflowResultDeliveryCredential? channelCredential = null,
        AgentToolExecutionOwner? executionOwner = null,
        string? botRegistrationId = null,
        AgentSkillRecoveryContext? skillRecovery = null) =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                ownerToken,
                "ambient-owner-org-token",
                senderToken,
                credentialKind,
                sourceReadableToken),
            Channel = new AgentToolChannelContext(
                "legacy-channel-platform",
                "ou-channel-alpha",
                "scope-channel-alpha",
                "message-alpha",
                null,
                null,
                channelCredential,
                botRegistrationId),
            SenderBinding = bindingId is null
                ? AgentToolSenderBindingContext.Empty
                : new AgentToolSenderBindingContext(
                    bindingId,
                    NyxUserId: "nyx-user-legacy-alpha",
                    SenderTenant: "tenant-channel-alpha"),
            NyxIdAuthority = authority ?? CompleteAuthority(),
            ExecutionOwner = executionOwner ?? new AgentToolExecutionOwner(),
            SkillRecovery = skillRecovery ?? AgentSkillRecoveryContext.Empty,
        });

    private static AgentToolNyxIdAuthorityContext CompleteAuthority() =>
        new(
            "lark",
            "tenant-authority-alpha",
            "ou-authority-alpha");

    private static ExternalSubjectRef CompleteSubject() =>
        new()
        {
            Platform = "lark",
            Tenant = "tenant-authority-alpha",
            ExternalUserId = "ou-authority-alpha",
        };
}
