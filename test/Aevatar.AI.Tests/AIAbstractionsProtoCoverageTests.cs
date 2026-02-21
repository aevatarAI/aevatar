using Aevatar.AI.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class AIAbstractionsProtoCoverageTests
{
    [Fact]
    public void ProtoMessages_ShouldRoundTripAndClone()
    {
        var request = RoundTrip(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "session-1",
            Metadata = { ["correlation_id"] = "c-1" },
        }, ChatRequestEvent.Parser);
        request.Metadata["correlation_id"].Should().Be("c-1");

        var response = RoundTrip(new ChatResponseEvent
        {
            Content = "world",
            SessionId = "session-1",
        }, ChatResponseEvent.Parser);
        response.Content.Should().Be("world");

        var textStart = RoundTrip(new TextMessageStartEvent
        {
            SessionId = "session-1",
            AgentId = "agent-1",
        }, TextMessageStartEvent.Parser);
        textStart.AgentId.Should().Be("agent-1");

        var textContent = RoundTrip(new TextMessageContentEvent
        {
            SessionId = "session-1",
            Delta = "delta",
        }, TextMessageContentEvent.Parser);
        textContent.Delta.Should().Be("delta");

        var textEnd = RoundTrip(new TextMessageEndEvent
        {
            SessionId = "session-1",
            Content = "done",
        }, TextMessageEndEvent.Parser);
        textEnd.Content.Should().Be("done");

        var toolCall = RoundTrip(new ToolCallEvent
        {
            ToolName = "search",
            ArgumentsJson = "{\"q\":\"x\"}",
            CallId = "call-1",
        }, ToolCallEvent.Parser);
        toolCall.ToolName.Should().Be("search");

        var toolResult = RoundTrip(new ToolResultEvent
        {
            CallId = "call-1",
            ResultJson = "{\"ok\":true}",
            Success = true,
            Error = "",
        }, ToolResultEvent.Parser);
        toolResult.Success.Should().BeTrue();

        var state = RoundTrip(new RoleGAgentState
        {
            RoleName = "assistant",
            MessageCount = 7,
        }, RoleGAgentState.Parser);
        state.RoleName.Should().Be("assistant");
        state.MessageCount.Should().Be(7);
    }

    [Fact]
    public void ProtoMessages_ShouldSupportMergeAndDescriptors()
    {
        var target = new ChatRequestEvent();
        target.MergeFrom(new ChatRequestEvent
        {
            Prompt = "p1",
            SessionId = "s1",
            Metadata = { ["k1"] = "v1" },
        });

        target.Prompt.Should().Be("p1");
        target.SessionId.Should().Be("s1");
        target.Metadata["k1"].Should().Be("v1");

        target.Clone().Should().BeEquivalentTo(target);
        target.ToString().Should().Contain("prompt");

        AiMessagesReflection.Descriptor.Should().NotBeNull();
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ChatRequestEvent));
    }

    [Fact]
    public void ProtoMessages_ShouldValidateNullAssignments()
    {
        var request = new ChatRequestEvent();
        var response = new ChatResponseEvent();
        var textStart = new TextMessageStartEvent();
        var toolCall = new ToolCallEvent();
        var toolResult = new ToolResultEvent();
        var state = new RoleGAgentState();

        Action setRequestPrompt = () => request.Prompt = null!;
        Action setResponseContent = () => response.Content = null!;
        Action setTextStartSession = () => textStart.SessionId = null!;
        Action setToolCallName = () => toolCall.ToolName = null!;
        Action setToolResultCallId = () => toolResult.CallId = null!;
        Action setStateRoleName = () => state.RoleName = null!;

        setRequestPrompt.Should().Throw<ArgumentNullException>();
        setResponseContent.Should().Throw<ArgumentNullException>();
        setTextStartSession.Should().Throw<ArgumentNullException>();
        setToolCallName.Should().Throw<ArgumentNullException>();
        setToolResultCallId.Should().Throw<ArgumentNullException>();
        setStateRoleName.Should().Throw<ArgumentNullException>();
    }

    private static T RoundTrip<T>(T message, MessageParser<T> parser)
        where T : class, IMessage<T>, new()
    {
        var bytes = message.ToByteArray();
        var parsed = parser.ParseFrom(bytes);
        parsed.Should().Be(message);

        var merged = new T();
        merged.MergeFrom(message);
        merged.Should().Be(message);

        return parsed;
    }
}
