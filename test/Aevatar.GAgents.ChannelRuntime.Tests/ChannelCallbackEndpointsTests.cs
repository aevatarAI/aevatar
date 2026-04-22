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

    [Fact]
    public void ShouldAcceptDirectLarkCallback_Returns_False_For_Nyx_Relay_Registration()
    {
        var registration = new ChannelBotRegistrationEntry
        {
            Platform = "lark",
            NyxAgentApiKeyId = "agent-key-1",
        };

        var accepted = ChannelCallbackEndpoints.ShouldAcceptDirectLarkCallback(
            registration,
            new LarkDirectWebhookCutoverOptions { AllowLegacyDirectCallback = true },
            new DateTimeOffset(2026, 4, 22, 0, 0, 0, TimeSpan.Zero));

        accepted.Should().BeFalse();
    }

    [Fact]
    public void ShouldAcceptDirectLarkCallback_Returns_True_Only_During_Rollback_Window_For_Legacy_Lark()
    {
        var registration = new ChannelBotRegistrationEntry
        {
            Platform = "lark",
        };

        var accepted = ChannelCallbackEndpoints.ShouldAcceptDirectLarkCallback(
            registration,
            new LarkDirectWebhookCutoverOptions
            {
                AllowLegacyDirectCallback = true,
                RollbackWindowEndsUtc = new DateTimeOffset(2026, 4, 23, 0, 0, 0, TimeSpan.Zero),
            },
            new DateTimeOffset(2026, 4, 22, 0, 0, 0, TimeSpan.Zero));

        accepted.Should().BeTrue();
    }

    [Fact]
    public void ShouldAcceptDirectLarkCallback_Returns_False_After_Rollback_Window_Closes()
    {
        var registration = new ChannelBotRegistrationEntry
        {
            Platform = "lark",
        };

        var accepted = ChannelCallbackEndpoints.ShouldAcceptDirectLarkCallback(
            registration,
            new LarkDirectWebhookCutoverOptions
            {
                AllowLegacyDirectCallback = true,
                RollbackWindowEndsUtc = new DateTimeOffset(2026, 4, 21, 0, 0, 0, TimeSpan.Zero),
            },
            new DateTimeOffset(2026, 4, 22, 0, 0, 0, TimeSpan.Zero));

        accepted.Should().BeFalse();
    }
}
