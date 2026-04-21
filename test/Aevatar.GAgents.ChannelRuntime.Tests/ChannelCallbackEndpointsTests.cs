using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelCallbackEndpointsTests
{
    [Fact]
    public void ResolveUpdatedRefreshToken_Preserves_Existing_Value_When_Request_Omits_RefreshToken()
    {
        var resolved = ChannelCallbackEndpoints.ResolveUpdatedRefreshToken(null, "refresh-old");

        resolved.Should().Be("refresh-old");
    }

    [Fact]
    public void ResolveUpdatedRefreshToken_Uses_Explicit_Value_When_Request_Provides_RefreshToken()
    {
        var resolved = ChannelCallbackEndpoints.ResolveUpdatedRefreshToken("refresh-new", "refresh-old");

        resolved.Should().Be("refresh-new");
    }
}
