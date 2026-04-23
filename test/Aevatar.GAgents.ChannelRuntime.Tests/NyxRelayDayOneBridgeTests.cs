using Aevatar.GAgents.NyxidChat.Relay;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxRelayDayOneBridgeTests
{
    [Theory]
    [InlineData("/daily alice", "private", true)]
    [InlineData("/daily alice", "group", true)]
    [InlineData("/daily alice", "channel", true)]
    [InlineData("/daily alice", null, true)]
    [InlineData("  /daily  ", "private", true)]
    [InlineData("/foobar", "private", true)]
    [InlineData("hello there", "private", false)]
    [InlineData("", "private", false)]
    [InlineData("   ", "private", false)]
    [InlineData("/daily alice", "device", false)]
    public void ShouldHandle_ReturnsExpectedGating(string text, string? conversationType, bool expected)
    {
        var bridge = new NyxRelayDayOneBridge(new ServiceCollection().BuildServiceProvider());
        var request = BuildRequest(text, conversationType);

        bridge.ShouldHandle(request).Should().Be(expected);
    }

    [Fact]
    public async Task HandleAsync_ForUnknownSlash_ReturnsUnknownCommandUsage()
    {
        var bridge = new NyxRelayDayOneBridge(new ServiceCollection().BuildServiceProvider());
        var request = BuildRequest("/daily_report alice", conversationType: "private");

        var reply = await bridge.HandleAsync(request, CancellationToken.None);

        reply.Should().Contain("Unknown command: /daily_report");
        reply.Should().Contain("/daily github_username=alice");
    }

    [Fact]
    public async Task HandleAsync_ForKnownCommandInGroup_ReturnsPrivateChatRestriction()
    {
        var bridge = new NyxRelayDayOneBridge(new ServiceCollection().BuildServiceProvider());
        var request = BuildRequest("/daily alice", conversationType: "group");

        var reply = await bridge.HandleAsync(request, CancellationToken.None);

        reply.Should().Contain("private chat");
        reply.Should().Contain("/daily");
    }

    [Fact]
    public async Task HandleAsync_ForKnownCommandInChannel_ReturnsPrivateChatRestriction()
    {
        var bridge = new NyxRelayDayOneBridge(new ServiceCollection().BuildServiceProvider());
        var request = BuildRequest("/daily alice", conversationType: "channel");

        var reply = await bridge.HandleAsync(request, CancellationToken.None);

        reply.Should().Contain("private chat");
    }

    [Fact]
    public async Task HandleAsync_ForKnownCommandWithoutArguments_ReturnsHelpText()
    {
        var bridge = new NyxRelayDayOneBridge(new ServiceCollection().BuildServiceProvider());
        var request = BuildRequest("/daily", conversationType: "private");

        var reply = await bridge.HandleAsync(request, CancellationToken.None);

        reply.Should().Contain("Daily report agent command");
        reply.Should().Contain("/daily github_username=alice");
    }

    [Fact]
    public async Task HandleAsync_ForCreateDailyWithArguments_InvokesAgentBuilderTool()
    {
        // No AgentBuilder runtime services wired → tool returns the expected DI-not-registered
        // error payload, which the bridge surfaces via the formatted tool-result helper. This
        // covers the full "decision.RequiresToolExecution" branch end-to-end.
        var bridge = new NyxRelayDayOneBridge(new ServiceCollection().BuildServiceProvider());
        var request = BuildRequest(
            "/daily github_username=alice schedule_time=09:00 repositories=owner/repo",
            conversationType: "private");

        var reply = await bridge.HandleAsync(request, CancellationToken.None);

        reply.Should().Contain("Create daily report agent failed");
    }

    private static NyxRelayBridgeRequest BuildRequest(string text, string? conversationType) =>
        new(
            Text: text,
            ConversationType: conversationType,
            ConversationId: "conv-1",
            MessageId: "msg-1",
            Platform: "lark",
            SenderId: "sender-1",
            SenderName: "Sender Name",
            ScopeId: "scope-1",
            NyxIdAccessToken: "token-1");
}
