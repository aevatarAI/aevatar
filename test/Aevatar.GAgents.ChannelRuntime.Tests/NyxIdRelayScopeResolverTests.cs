using FluentAssertions;
using NSubstitute;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdRelayScopeResolverTests
{
    [Fact]
    public async Task ResolveScopeIdByApiKeyAsync_ShouldReturnRegistrationScopeId()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        queryPort.ListByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
            [
                new ChannelBotRegistrationEntry { ScopeId = "scope-1" },
            ]));
        var resolver = new NyxIdRelayScopeResolver(queryPort);

        var result = await resolver.ResolveScopeIdByApiKeyAsync(" nyx-key-1 ");

        result.Should().Be("scope-1");
        await queryPort.Received(1).ListByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveScopeIdByApiKeyAsync_ShouldReturnNull_WhenNoRegistrationMatches()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        queryPort.ListByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(Array.Empty<ChannelBotRegistrationEntry>()));
        var resolver = new NyxIdRelayScopeResolver(queryPort);

        var result = await resolver.ResolveScopeIdByApiKeyAsync("nyx-key-1");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveScopeIdByApiKeyAsync_ShouldReturnNull_WhenAllRegistrationsHaveEmptyScope()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        queryPort.ListByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
            [
                new ChannelBotRegistrationEntry { ScopeId = string.Empty },
                new ChannelBotRegistrationEntry { ScopeId = "   " },
            ]));
        var resolver = new NyxIdRelayScopeResolver(queryPort);

        var result = await resolver.ResolveScopeIdByApiKeyAsync("nyx-key-1");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveScopeIdByApiKeyAsync_ShouldCollapseDuplicates_WhenRegistrationsAgreeOnScope()
    {
        // Repeated mirror flows can persist multiple ChannelBotRegistration documents with
        // the same NyxAgentApiKeyId; if they all agree on ScopeId, that's still a single
        // tenant and the resolver should return it.
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        queryPort.ListByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
            [
                new ChannelBotRegistrationEntry { Id = "reg-a", ScopeId = "scope-1" },
                new ChannelBotRegistrationEntry { Id = "reg-b", ScopeId = "scope-1" },
            ]));
        var resolver = new NyxIdRelayScopeResolver(queryPort);

        var result = await resolver.ResolveScopeIdByApiKeyAsync("nyx-key-1");

        result.Should().Be("scope-1");
    }

    [Fact]
    public async Task ResolveScopeIdByApiKeyAsync_ShouldRefuse_WhenRegistrationsResolveToDifferentScopes()
    {
        // Cross-tenant safety: if the registration store has multiple entries sharing the
        // same NyxAgentApiKeyId but pointing at different ScopeIds (e.g., a botched repair
        // that left a stale tenant entry), routing the relay turn to either scope would be
        // a tenant-isolation violation. Refuse the resolution and let the endpoint 401.
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        queryPort.ListByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>(
            [
                new ChannelBotRegistrationEntry { Id = "reg-tenant-a", ScopeId = "scope-tenant-a" },
                new ChannelBotRegistrationEntry { Id = "reg-tenant-b", ScopeId = "scope-tenant-b" },
            ]));
        var resolver = new NyxIdRelayScopeResolver(queryPort);

        var result = await resolver.ResolveScopeIdByApiKeyAsync("nyx-key-1");

        result.Should().BeNull();
    }
}
