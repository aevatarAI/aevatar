using FluentAssertions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdRelayScopeResolverTests
{
    [Fact]
    public async Task ResolveScopeIdByApiKeyAsync_ShouldReturnRegistrationScopeId()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        queryPort.GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                ScopeId = "scope-1",
            }));
        var resolver = new NyxIdRelayScopeResolver(queryPort);

        var result = await resolver.ResolveScopeIdByApiKeyAsync(" nyx-key-1 ");

        result.Should().Be("scope-1");
        await queryPort.Received(1).GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveScopeIdByApiKeyAsync_ShouldReturnNull_WhenRegistrationHasNoScope()
    {
        var queryPort = Substitute.For<IChannelBotRegistrationQueryByNyxIdentityPort>();
        queryPort.GetByNyxAgentApiKeyIdAsync("nyx-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry()));
        var resolver = new NyxIdRelayScopeResolver(queryPort);

        var result = await resolver.ResolveScopeIdByApiKeyAsync("nyx-key-1");

        result.Should().BeNull();
    }
}
