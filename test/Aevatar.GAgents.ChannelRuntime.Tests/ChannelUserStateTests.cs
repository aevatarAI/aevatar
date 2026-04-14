using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Tests for ChannelUserState proto message behavior.
/// Validates Clone semantics and field defaults that the state transitions rely on.
/// </summary>
public class ChannelUserStateTests
{
    [Fact]
    public void NewState_AllFieldsEmpty()
    {
        var state = new ChannelUserState();

        state.Platform.Should().BeEmpty();
        state.PlatformUserId.Should().BeEmpty();
        state.DisplayName.Should().BeEmpty();
        state.NyxidUserId.Should().BeEmpty();
        state.NyxidAccessToken.Should().BeEmpty();
        state.FirstSeen.Should().BeNull();
        state.LastSeen.Should().BeNull();
    }

    [Fact]
    public void Clone_ProducesIndependentCopy()
    {
        var original = new ChannelUserState
        {
            Platform = "telegram",
            PlatformUserId = "12345",
            DisplayName = "Alice",
            NyxidUserId = "nyx-alice",
            NyxidAccessToken = "token-alice",
            FirstSeen = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            LastSeen = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        var clone = original.Clone();

        // Values match
        clone.Platform.Should().Be("telegram");
        clone.PlatformUserId.Should().Be("12345");
        clone.DisplayName.Should().Be("Alice");
        clone.NyxidUserId.Should().Be("nyx-alice");
        clone.NyxidAccessToken.Should().Be("token-alice");

        // Mutation doesn't affect original
        clone.DisplayName = "Bob";
        original.DisplayName.Should().Be("Alice");
    }

    [Fact]
    public void TrackedEvent_RoundTrip()
    {
        var evt = new ChannelUserTrackedEvent
        {
            Platform = "lark",
            PlatformUserId = "ou_xxx",
            DisplayName = "Charlie",
        };

        evt.Platform.Should().Be("lark");
        evt.PlatformUserId.Should().Be("ou_xxx");
        evt.DisplayName.Should().Be("Charlie");
    }

    [Fact]
    public void BoundEvent_RoundTrip()
    {
        var evt = new ChannelUserBoundEvent
        {
            NyxidUserId = "nyx-user-1",
            NyxidAccessToken = "tok-abc",
        };

        evt.NyxidUserId.Should().Be("nyx-user-1");
        evt.NyxidAccessToken.Should().Be("tok-abc");
    }

    [Fact]
    public void InboundEvent_AllFieldsPopulated()
    {
        var evt = new ChannelInboundEvent
        {
            Text = "hello",
            SenderId = "12345",
            SenderName = "Alice",
            ConversationId = "chat-1",
            MessageId = "msg-1",
            ChatType = "private",
            Platform = "telegram",
            RegistrationId = "reg-1",
            RegistrationToken = "org-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-telegram-bot",
        };

        evt.Text.Should().Be("hello");
        evt.RegistrationToken.Should().Be("org-token");
        evt.NyxProviderSlug.Should().Be("api-telegram-bot");
    }

    [Fact]
    public void InboundEvent_EmptyOptionalFields_DefaultToEmpty()
    {
        var evt = new ChannelInboundEvent
        {
            Text = "hello",
            SenderId = "12345",
            SenderName = "Alice",
            ConversationId = "chat-1",
            Platform = "telegram",
            RegistrationId = "reg-1",
            RegistrationToken = "org-token",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "api-telegram-bot",
        };

        evt.MessageId.Should().BeEmpty();
        evt.ChatType.Should().BeEmpty();
    }
}
