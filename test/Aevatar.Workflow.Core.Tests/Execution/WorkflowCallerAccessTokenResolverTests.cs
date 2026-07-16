using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowCallerAccessTokenResolverTests
{
    [Fact]
    public async Task ResolveAsync_WithBearerAndAuthority_ShouldPreserveBearerWithoutIssuance()
    {
        var provider = new RecordingAccessTokenProvider();
        var credential = new WorkflowCallerCredential
        {
            BearerToken = "interactive-token",
            NyxIdAuthority = CreateAuthority(),
        };

        var resolved = await WorkflowCallerAccessTokenResolver.ResolveAsync(
            credential,
            provider,
            CancellationToken.None);

        resolved.Should().BeSameAs(credential);
        resolved.BearerToken.Should().Be("interactive-token");
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

        resolved.BearerToken.Should().Be("issued-token");
        resolved.NyxIdAuthority.Should().BeEquivalentTo(authority);
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
            return Task.FromResult("issued-token");
        }
    }
}
