using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Bindings;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class DefaultMemberPublishedServiceResolverTests
{
    [Fact]
    public async Task ResolveAsync_ShouldMapMemberToStablePublishedServiceId()
    {
        var resolver = new DefaultMemberPublishedServiceResolver();

        var result = await resolver.ResolveAsync(new MemberPublishedServiceResolveRequest(
            " scope-a ",
            " member-a "));

        result.ScopeId.Should().Be("scope-a");
        result.MemberId.Should().Be("member-a");
        result.PublishedServiceId.Should().Be("member-a");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectBlankMemberId()
    {
        var resolver = new DefaultMemberPublishedServiceResolver();

        var act = () => resolver.ResolveAsync(new MemberPublishedServiceResolveRequest("scope-a", " "));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MemberId is required*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectMemberIdThatBreaksServiceKeySegments()
    {
        var resolver = new DefaultMemberPublishedServiceResolver();

        var act = () => resolver.ResolveAsync(new MemberPublishedServiceResolveRequest("scope-a", "foo:bar"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*memberId must not contain*");
    }
}
