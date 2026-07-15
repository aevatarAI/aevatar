using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Hosting.DependencyInjection;
using Aevatar.GAgentService.Infrastructure.Credentials;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class NyxIdWorkflowCallerAccessTokenProviderTests
{
    [Fact]
    public async Task IssueAsync_WithoutBroker_ShouldComposeAndFailClosedWhenTokenIsRequested()
    {
        var services = new ServiceCollection();
        services.AddGAgentServiceCapability(new ConfigurationBuilder().Build());

        using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<IWorkflowCallerAccessTokenProvider>();

        provider.Should().BeOfType<NyxIdWorkflowCallerAccessTokenProvider>();

        var act = () => provider.IssueAsync(new WorkflowCallerNyxIdAuthority
        {
            Platform = "nyxid",
            Tenant = "tenant-1",
            ExternalUserId = "user-1",
            Scope = "invoke",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires a configured NyxID capability broker*");
    }

    [Fact]
    public async Task IssueAsync_ShouldRequestFreshTokenForEveryCall()
    {
        var broker = new RotatingCapabilityBroker();
        var provider = new NyxIdWorkflowCallerAccessTokenProvider(broker);
        var authority = new WorkflowCallerNyxIdAuthority
        {
            Platform = "nyxid",
            Tenant = "tenant-1",
            ExternalUserId = "user-1",
            Scope = "invoke",
        };

        var first = await provider.IssueAsync(authority);
        var second = await provider.IssueAsync(authority);

        first.Should().Be("token-1");
        second.Should().Be("token-2");
        broker.Requests.Should().HaveCount(2);
        broker.Requests.Should().OnlyContain(request =>
            request.Subject.ExternalUserId == "user-1" && request.Scope.Value == "invoke");
    }

    [Fact]
    public async Task IssueAsync_ShouldFailClosed_WhenAuthorityIsIncomplete()
    {
        var provider = new NyxIdWorkflowCallerAccessTokenProvider(new RotatingCapabilityBroker());

        var act = () => provider.IssueAsync(new WorkflowCallerNyxIdAuthority
        {
            Platform = "nyxid",
            ExternalUserId = "user-1",
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task IssueAsync_ShouldFailClosed_WhenBrokerReturnsWhitespaceToken()
    {
        var provider = new NyxIdWorkflowCallerAccessTokenProvider(new EmptyTokenCapabilityBroker());

        var act = () => provider.IssueAsync(new WorkflowCallerNyxIdAuthority
        {
            Platform = "nyxid",
            Tenant = "tenant-1",
            ExternalUserId = "user-1",
            Scope = "invoke",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned an empty access token*");
    }

    private sealed class RotatingCapabilityBroker : INyxIdCapabilityBroker
    {
        public List<(ExternalSubjectRef Subject, CapabilityScope Scope)> Requests { get; } = [];

        public Task<BindingChallenge> StartExternalBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task RevokeBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default)
        {
            Requests.Add((externalSubject.Clone(), scope.Clone()));
            var sequence = Requests.Count;
            return Task.FromResult(new CapabilityHandle
            {
                AccessToken = $"token-{sequence}",
                ExpiresAtUnix = sequence,
            });
        }

        public Task<CapabilityHandle> IssueShortLivedByBindingIdAsync(
            ExternalSubjectRef externalSubject,
            string bindingId,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            IssueShortLivedAsync(externalSubject, scope, ct);
    }

    private sealed class EmptyTokenCapabilityBroker : INyxIdCapabilityBroker
    {
        public Task<BindingChallenge> StartExternalBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task RevokeBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            Task.FromResult(new CapabilityHandle { AccessToken = "   " });

        public Task<CapabilityHandle> IssueShortLivedByBindingIdAsync(
            ExternalSubjectRef externalSubject,
            string bindingId,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            IssueShortLivedAsync(externalSubject, scope, ct);
    }
}
