using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowCallerAccessTokenResolverTests
{
    [Fact]
    public async Task ResolveAsync_WithBearerAndAuthority_ShouldIssueFreshBearerForEveryCall()
    {
        var provider = new RecordingAccessTokenProvider();
        var credential = new WorkflowCallerCredential
        {
            BearerToken = "short-lived-token",
            SourceReadableUserBearerToken = "source-readable-token",
            NyxIdAuthority = CreateAuthority(),
            Kind = NyxIdCallerCredentialKind.ProxyDelegation,
        };

        var first = await WorkflowCallerAccessTokenResolver.ResolveAsync(
            credential,
            provider,
            CancellationToken.None);
        var second = await WorkflowCallerAccessTokenResolver.ResolveAsync(
            credential,
            provider,
            CancellationToken.None);

        first.Should().NotBeSameAs(credential);
        first.BearerToken.Should().Be("issued-token-1");
        second.BearerToken.Should().Be("issued-token-2");
        first.SourceReadableUserBearerToken.Should().Be("source-readable-token");
        first.NyxIdAuthority.Should().BeEquivalentTo(credential.NyxIdAuthority);
        first.Kind.Should().Be(NyxIdCallerCredentialKind.ProxyDelegation);
        provider.IssueCount.Should().Be(2);
    }

    [Fact]
    public async Task ResolveAsync_WithDirectUserBearerAndAuthority_ShouldPreserveBearer()
    {
        var provider = new RecordingAccessTokenProvider();
        var credential = new WorkflowCallerCredential
        {
            BearerToken = "interactive-token",
            NyxIdAuthority = CreateAuthority(),
            Kind = NyxIdCallerCredentialKind.SourceReadableUserBearer,
        };

        var resolved = await WorkflowCallerAccessTokenResolver.ResolveAsync(
            credential,
            provider,
            CancellationToken.None);

        resolved.Should().BeSameAs(credential);
        provider.IssueCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithWebhookBindingAgentKey_ShouldPreserveExactBearer()
    {
        var provider = new RecordingAccessTokenProvider();
        var credential = new WorkflowCallerCredential
        {
            BearerToken = "nyxid_ag_exact_service_secret",
            DurableCallerCredential = new DurableCallerCredentialRef
            {
                Ref = "sec-webhook-binding",
                Purpose = CredentialSecretPurposes.WorkflowWebhookBindingAgentKey,
                OwnerScopeKey = "scope-1",
                SubjectId = "owner-alpha",
                SourceKind = DurableCallerCredentialSourceKind.WebhookBinding,
                ProviderCredentialId = "provider-key-1",
            },
            NyxIdAuthority = CreateAuthority(),
            Kind = NyxIdCallerCredentialKind.AgentKey,
        };

        var resolved = await WorkflowCallerAccessTokenResolver.ResolveAsync(
            credential,
            provider,
            CancellationToken.None);

        resolved.Should().BeSameAs(credential);
        resolved.BearerToken.Should().Be("nyxid_ag_exact_service_secret");
        provider.IssueCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithScheduledInvocationAgentKey_ShouldPreserveExactBearer()
    {
        var provider = new RecordingAccessTokenProvider();
        var credential = new WorkflowCallerCredential
        {
            BearerToken = "nyxid_ag_scheduled_service_secret",
            DurableCallerCredential = new DurableCallerCredentialRef
            {
                Ref = "sec-scheduled-agent-key",
                Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
                OwnerScopeKey = "scope-scheduled",
                SubjectId = "agent-key-scheduled",
                SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
            },
            NyxIdAuthority = CreateAuthority(),
            Kind = NyxIdCallerCredentialKind.AgentKey,
        };

        var resolved = await WorkflowCallerAccessTokenResolver.ResolveAsync(
            credential,
            provider,
            CancellationToken.None);

        resolved.Should().BeSameAs(credential);
        resolved.BearerToken.Should().Be("nyxid_ag_scheduled_service_secret");
        resolved.Kind.Should().Be(NyxIdCallerCredentialKind.AgentKey);
        provider.IssueCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithMismatchedAgentKeyReference_ShouldFailClosed()
    {
        var provider = new RecordingAccessTokenProvider();
        var credential = new WorkflowCallerCredential
        {
            BearerToken = "nyxid_ag_mismatched_secret",
            DurableCallerCredential = new DurableCallerCredentialRef
            {
                Ref = "sec-scheduled-bearer",
                Purpose = CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
                OwnerScopeKey = "scope-scheduled",
                SubjectId = "scheduled-user",
                SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
            },
            NyxIdAuthority = CreateAuthority(),
            Kind = NyxIdCallerCredentialKind.AgentKey,
        };

        var act = () => WorkflowCallerAccessTokenResolver.ResolveAsync(
            credential,
            provider,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match a supported durable vault reference*");
        provider.IssueCount.Should().Be(0);
    }

    [Theory]
    [InlineData("nyxid_ag_channel_agent_key")]
    [InlineData("")]
    public async Task ResolveAsync_WithChannelAgentKey_ShouldNeverIssueUserToken(string agentKey)
    {
        var provider = new RecordingAccessTokenProvider();
        var credential = new WorkflowCallerCredential
        {
            BearerToken = agentKey,
            DurableCallerCredential = new DurableCallerCredentialRef
            {
                Ref = "sec-channel-agent-key",
                Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey,
                OwnerScopeKey = "scope-channel",
                SubjectId = "agent-key-channel",
                SourceKind = DurableCallerCredentialSourceKind.ChannelRegistration,
            },
            NyxIdAuthority = CreateAuthority(),
            Kind = NyxIdCallerCredentialKind.AgentKey,
        };

        var resolved = await WorkflowCallerAccessTokenResolver.ResolveAsync(
            credential,
            provider,
            CancellationToken.None);

        resolved.Should().BeSameAs(credential);
        resolved.BearerToken.Should().Be(agentKey);
        provider.IssueCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_WithBearerOnly_ShouldPreserveBearer()
    {
        var credential = new WorkflowCallerCredential { BearerToken = "interactive-token" };

        var resolved = await WorkflowCallerAccessTokenResolver.ResolveAsync(
            credential,
            provider: null,
            CancellationToken.None);

        resolved.Should().BeSameAs(credential);
    }

    [Fact]
    public async Task ResolveAsync_WithAuthorityOnly_ShouldIssueBearer()
    {
        var provider = new RecordingAccessTokenProvider();
        var authority = CreateAuthority();

        var resolved = await WorkflowCallerAccessTokenResolver.ResolveAsync(
            new WorkflowCallerCredential { NyxIdAuthority = authority },
            provider,
            CancellationToken.None);

        resolved.BearerToken.Should().Be("issued-token-1");
        resolved.NyxIdAuthority.Should().BeEquivalentTo(authority);
        resolved.Kind.Should().Be(NyxIdCallerCredentialKind.ProxyDelegation);
        provider.IssueCount.Should().Be(1);
        provider.LastAuthority.Should().BeSameAs(authority);
    }

    [Fact]
    public async Task ResolveAsync_WithAuthorityOnlyAndNoProvider_ShouldFailClosed()
    {
        var act = () => WorkflowCallerAccessTokenResolver.ResolveAsync(
            new WorkflowCallerCredential { NyxIdAuthority = CreateAuthority() },
            provider: null,
            CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*access token provider is unavailable*");
    }

    private static WorkflowCallerNyxIdAuthority CreateAuthority() =>
        new()
        {
            Platform = "nyxid",
            Tenant = "tenant-1",
            ExternalUserId = "m-alpha",
            Scope = "proxy",
        };

    private sealed class RecordingAccessTokenProvider : IWorkflowCallerAccessTokenProvider
    {
        public int IssueCount { get; private set; }

        public WorkflowCallerNyxIdAuthority? LastAuthority { get; private set; }

        public Task<string> IssueAsync(
            WorkflowCallerNyxIdAuthority authority,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            IssueCount++;
            LastAuthority = authority;
            return Task.FromResult($"issued-token-{IssueCount}");
        }
    }
}
