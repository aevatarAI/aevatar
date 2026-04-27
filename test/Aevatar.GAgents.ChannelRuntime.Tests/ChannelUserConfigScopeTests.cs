using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelUserConfigScopeTests
{
    [Fact]
    public void FromInboundEvent_WithSenderId_ComposesPerUserScope()
    {
        // Two Lark users sharing one bot must produce different user-config scopes,
        // otherwise their saved github_username preferences overwrite each other
        // (issue #436).
        var alice = new ChannelInboundEvent
        {
            RegistrationScopeId = "bot-scope-1",
            Platform = "lark",
            SenderId = "ou_alice",
        };
        var bob = new ChannelInboundEvent
        {
            RegistrationScopeId = "bot-scope-1",
            Platform = "lark",
            SenderId = "ou_bob",
        };

        var aliceScope = ChannelUserConfigScope.FromInboundEvent(alice);
        var bobScope = ChannelUserConfigScope.FromInboundEvent(bob);

        aliceScope.Should().Be("bot-scope-1:lark:ou_alice");
        bobScope.Should().Be("bot-scope-1:lark:ou_bob");
        aliceScope.Should().NotBe(bobScope);
    }

    [Fact]
    public void FromInboundEvent_NoSenderId_FallsBackToRegistrationScope()
    {
        // Programmatic / system inbound paths without an end-user identity keep the
        // existing bot-scoped behavior.
        var evt = new ChannelInboundEvent
        {
            RegistrationScopeId = "bot-scope-1",
            Platform = "lark",
        };

        ChannelUserConfigScope.FromInboundEvent(evt).Should().Be("bot-scope-1");
    }

    [Fact]
    public void FromInboundEvent_EmptyRegistrationScope_DefaultsToDefault()
    {
        var evt = new ChannelInboundEvent
        {
            Platform = "lark",
            SenderId = "ou_alice",
        };

        ChannelUserConfigScope.FromInboundEvent(evt).Should().Be("default:lark:ou_alice");
    }

    [Fact]
    public void FromInboundEvent_EmptyPlatform_UsesChannelLiteral()
    {
        // Channel-neutral fallback when the inbound platform tag is missing — keeps
        // the composite key well-formed even on synthetic test fixtures.
        var evt = new ChannelInboundEvent
        {
            RegistrationScopeId = "bot-scope-1",
            SenderId = "ou_alice",
        };

        ChannelUserConfigScope.FromInboundEvent(evt).Should().Be("bot-scope-1:channel:ou_alice");
    }

    [Fact]
    public void FromInboundEvent_PlatformIsLowerCased_SoCasingDifferencesShareOneActor()
    {
        // Same human + same bot reaching us via two casings of the platform tag must
        // collapse to the same scope; otherwise they'd see different saved preferences
        // depending on adapter capitalization.
        var lower = new ChannelInboundEvent
        {
            RegistrationScopeId = "bot-scope-1",
            Platform = "lark",
            SenderId = "ou_alice",
        };
        var upper = new ChannelInboundEvent
        {
            RegistrationScopeId = "bot-scope-1",
            Platform = "Lark",
            SenderId = "ou_alice",
        };

        ChannelUserConfigScope.FromInboundEvent(lower)
            .Should().Be(ChannelUserConfigScope.FromInboundEvent(upper));
    }

    [Fact]
    public void FromMetadata_BuildsSameScopeAsInboundEvent()
    {
        // The tool path receives the same fields via AgentToolRequestContext.CurrentMetadata
        // rather than a ChannelInboundEvent. Both code paths must agree on the scope key
        // — otherwise the form prefill would read one actor and the preference save
        // would write to another.
        var evt = new ChannelInboundEvent
        {
            RegistrationScopeId = "bot-scope-1",
            Platform = "lark",
            SenderId = "ou_alice",
        };
        var metadata = new Dictionary<string, string>
        {
            ["scope_id"] = "bot-scope-1",
            [ChannelMetadataKeys.Platform] = "lark",
            [ChannelMetadataKeys.SenderId] = "ou_alice",
        };

        ChannelUserConfigScope.FromMetadata(metadata)
            .Should().Be(ChannelUserConfigScope.FromInboundEvent(evt));
    }

    [Fact]
    public void FromMetadata_NullMetadata_ReturnsDefault()
    {
        ChannelUserConfigScope.FromMetadata(null).Should().Be("default");
    }
}
