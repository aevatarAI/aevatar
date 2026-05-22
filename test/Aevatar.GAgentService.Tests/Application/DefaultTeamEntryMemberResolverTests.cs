using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Bindings;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class DefaultTeamEntryMemberResolverTests
{
    [Fact]
    public async Task ResolveAsync_ShouldKeepTransitionalDeterministicMappingUntilTeamMigratesToStudio()
    {
        var resolver = new DefaultTeamEntryMemberResolver();

        var result = await resolver.ResolveAsync(" scope-a ", " team-a ");

        result.ScopeId.Should().Be("scope-a");
        result.TeamId.Should().Be("team-a");
        result.EntryMemberId.Should().Be("team-a");
        result.PublishedServiceId.Should().Be("team-a");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectBlankTeamId()
    {
        var resolver = new DefaultTeamEntryMemberResolver();

        var act = () => resolver.ResolveAsync("scope-a", " ");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*teamId is required*");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectTeamIdThatBreaksServiceKeySegments()
    {
        var resolver = new DefaultTeamEntryMemberResolver();

        var act = () => resolver.ResolveAsync("scope-a", "foo:bar");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*teamId must not contain*");
    }
}
