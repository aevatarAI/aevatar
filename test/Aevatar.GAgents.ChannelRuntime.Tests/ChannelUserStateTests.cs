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
    public void NewState_ProcessedMessageIds_IsEmpty()
    {
        var state = new ChannelUserState();
        state.ProcessedMessageIds.Should().BeEmpty();
    }

    [Fact]
    public void ProcessedMessageIds_CanAddAndRetain()
    {
        var state = new ChannelUserState();
        state.ProcessedMessageIds.Add("msg-1");
        state.ProcessedMessageIds.Add("msg-2");

        state.ProcessedMessageIds.Should().HaveCount(2);
        state.ProcessedMessageIds.Should().Contain("msg-1");
        state.ProcessedMessageIds.Should().Contain("msg-2");
    }

    [Fact]
    public void ProcessedMessageIds_SurvivesClone()
    {
        var original = new ChannelUserState();
        original.ProcessedMessageIds.Add("msg-a");
        original.ProcessedMessageIds.Add("msg-b");

        var clone = original.Clone();

        clone.ProcessedMessageIds.Should().HaveCount(2);
        clone.ProcessedMessageIds.Should().Contain("msg-a");
        clone.ProcessedMessageIds.Should().Contain("msg-b");

        // Mutation isolation
        clone.ProcessedMessageIds.Add("msg-c");
        original.ProcessedMessageIds.Should().HaveCount(2);
    }

    [Fact]
    public void ProcessedMessageIds_RemoveAt_Removes_Oldest()
    {
        var state = new ChannelUserState();
        state.ProcessedMessageIds.Add("first");
        state.ProcessedMessageIds.Add("second");
        state.ProcessedMessageIds.Add("third");

        state.ProcessedMessageIds.RemoveAt(0);

        state.ProcessedMessageIds.Should().HaveCount(2);
        state.ProcessedMessageIds[0].Should().Be("second");
        state.ProcessedMessageIds[1].Should().Be("third");
    }

    [Fact]
    public void ChannelBotRegistrationEntry_RelayIdentifiers_RoundTrip()
    {
        var entry = new ChannelBotRegistrationEntry
        {
            Id = "test",
            NyxChannelBotId = "bot-1",
            NyxAgentApiKeyId = "key-1",
            NyxConversationRouteId = "route-1",
        };

        var clone = entry.Clone();
        clone.NyxChannelBotId.Should().Be("bot-1");
        clone.NyxAgentApiKeyId.Should().Be("key-1");
        clone.NyxConversationRouteId.Should().Be("route-1");
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
