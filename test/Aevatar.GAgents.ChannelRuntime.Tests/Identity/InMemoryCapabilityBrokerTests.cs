using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour pinning for <see cref="InMemoryCapabilityBroker"/> so the test
/// fake stays a faithful stand-in for the production broker contract pinned
/// in ADR-0018 §INyxIdCapabilityBroker.
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
    public async Task QueryPort_ResolveAsync_ReturnsNullForUnboundSubject()
    {
        IExternalIdentityBindingQueryPort broker = new InMemoryCapabilityBroker();

        var result = await broker.ResolveAsync(SampleSubject());

        result.Should().BeNull();
    }

    [Fact]
    public async Task QueryPort_ResolveAsync_ReturnsSeededBinding()
    {
        var fake = new InMemoryCapabilityBroker();
        var subject = SampleSubject();
        fake.SeedBinding(subject, new BindingId { Value = "bnd_x" });

        // Read through the query-port seam — broker no longer exposes a
        // resolve method (ADR-0018 §INyxIdCapabilityBroker is write-only).
        IExternalIdentityBindingQueryPort port = fake;
        var result = await port.ResolveAsync(subject);

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
    public async Task IssueShortLivedAsync_ThrowsBindingNotFoundWhenUnbound()
    {
        // Distinct from BindingRevokedException — "never bound" is a different
        // semantic than "previously bound, NyxID revoked it".
        var broker = new InMemoryCapabilityBroker();

        var act = () => broker.IssueShortLivedAsync(
            SampleSubject(),
            new CapabilityScope { Value = "openid" });

        await act.Should().ThrowAsync<BindingNotFoundException>();
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
        var fake = new InMemoryCapabilityBroker();
        var subject = SampleSubject();
        fake.SeedBinding(subject, new BindingId { Value = "bnd_x" });

        INyxIdCapabilityBroker broker = fake;
        await broker.RevokeBindingAsync(subject);

        IExternalIdentityBindingQueryPort port = fake;
        var result = await port.ResolveAsync(subject);
        result.Should().BeNull();
    }
}
