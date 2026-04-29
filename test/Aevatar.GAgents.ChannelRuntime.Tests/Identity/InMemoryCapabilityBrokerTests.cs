using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour pinning for <see cref="InMemoryCapabilityBroker"/> so the test
/// fake stays a faithful stand-in for the production broker contract pinned
/// in ADR-0017 §INyxIdCapabilityBroker.
/// </summary>
public class InMemoryCapabilityBrokerTests
{
    private static ExternalSubjectRef SampleSubject(string user = "ou_user_y") => new()
    {
        Platform = "lark",
        Tenant = "ou_tenant_x",
        ExternalUserId = user,
    };

    [Fact]
    public async Task ResolveBindingAsync_ReturnsNullForUnboundSubject()
    {
        var broker = new InMemoryCapabilityBroker();

        var result = await broker.ResolveBindingAsync(SampleSubject());

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveBindingAsync_ReturnsSeededBinding()
    {
        var broker = new InMemoryCapabilityBroker();
        var subject = SampleSubject();
        broker.SeedBinding(subject, new BindingId { Value = "bnd_x" });

        var result = await broker.ResolveBindingAsync(subject);

        result.Should().NotBeNull();
        result!.Value.Should().Be("bnd_x");
    }

    [Fact]
    public async Task IssueShortLivedAsync_ReturnsHandleForActiveBinding()
    {
        var broker = new InMemoryCapabilityBroker();
        var subject = SampleSubject();
        broker.SeedBinding(subject, new BindingId { Value = "bnd_x" });

        var handle = await broker.IssueShortLivedAsync(
            subject,
            new CapabilityScope { Value = "openid" });

        handle.AccessToken.Should().NotBeNullOrEmpty();
        handle.Scope.Should().Be("openid");
    }

    [Fact]
    public async Task IssueShortLivedAsync_ThrowsBindingRevokedWhenUnbound()
    {
        var broker = new InMemoryCapabilityBroker();

        var act = () => broker.IssueShortLivedAsync(
            SampleSubject(),
            new CapabilityScope { Value = "openid" });

        await act.Should().ThrowAsync<BindingRevokedException>();
    }

    [Fact]
    public async Task IssueShortLivedAsync_ThrowsBindingRevokedAfterNyxIdRevoke()
    {
        var broker = new InMemoryCapabilityBroker();
        var subject = SampleSubject();
        var bindingId = new BindingId { Value = "bnd_x" };
        broker.SeedBinding(subject, bindingId);
        broker.MarkRevokedOnNyxId(bindingId);

        var act = () => broker.IssueShortLivedAsync(
            subject,
            new CapabilityScope { Value = "openid" });

        await act.Should().ThrowAsync<BindingRevokedException>()
            .Where(ex => ex.ExternalSubject.ExternalUserId == subject.ExternalUserId);
    }

    [Fact]
    public async Task RevokeBindingAsync_RemovesBinding()
    {
        var broker = new InMemoryCapabilityBroker();
        var subject = SampleSubject();
        broker.SeedBinding(subject, new BindingId { Value = "bnd_x" });

        await broker.RevokeBindingAsync(subject);

        var result = await broker.ResolveBindingAsync(subject);
        result.Should().BeNull();
    }
}
