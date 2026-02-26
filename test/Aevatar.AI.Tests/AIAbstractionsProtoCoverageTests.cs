using Aevatar.AI.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

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

        var configure = RoundTrip(new ConfigureRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            AppConfigJson = "{\"tenant\":\"a\"}",
            AppConfigCodec = RoleGAgentExtensionContract.AppConfigCodecJsonPlain,
            AppConfigSchemaVersion = 3,
        }, ConfigureRoleAgentEvent.Parser);
        configure.AppConfigCodec.Should().Be(RoleGAgentExtensionContract.AppConfigCodecJsonPlain);

        var appConfigEvent = RoundTrip(new SetRoleAppConfigEvent
        {
            AppConfigJson = "{\"tenant\":\"b\"}",
            AppConfigCodec = RoleGAgentExtensionContract.AppConfigCodecJsonPlain,
            AppConfigSchemaVersion = 4,
        }, SetRoleAppConfigEvent.Parser);
        appConfigEvent.AppConfigSchemaVersion.Should().Be(4);

        var appStateEvent = RoundTrip(new SetRoleAppStateEvent
        {
            AppState = Any.Pack(new ChatResponseEvent { Content = "payload", SessionId = "session-1" }),
            AppStateCodec = RoleGAgentExtensionContract.AppStateCodecProtobufAny,
            AppStateSchemaVersion = 2,
        }, SetRoleAppStateEvent.Parser);
        appStateEvent.AppStateCodec.Should().Be(RoleGAgentExtensionContract.AppStateCodecProtobufAny);

        var state = RoundTrip(new RoleGAgentState
        {
            RoleName = "assistant",
            MessageCount = 7,
            AppState = Any.Pack(new ChatRequestEvent { Prompt = "state", SessionId = "session-2" }),
            AppStateCodec = RoleGAgentExtensionContract.AppStateCodecProtobufAny,
            AppStateSchemaVersion = 5,
            AppConfigJson = "{\"tenant\":\"z\"}",
            AppConfigCodec = RoleGAgentExtensionContract.AppConfigCodecJsonPlain,
            AppConfigSchemaVersion = 6,
        }, RoleGAgentState.Parser);
        state.RoleName.Should().Be("assistant");
        state.MessageCount.Should().Be(7);
        state.AppStateSchemaVersion.Should().Be(5);
        state.AppConfigCodec.Should().Be(RoleGAgentExtensionContract.AppConfigCodecJsonPlain);
        state.AppConfigSchemaVersion.Should().Be(6);
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
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ChatResponseEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(TextMessageStartEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(TextMessageContentEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(TextMessageEndEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ToolCallEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ToolResultEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(SetRoleAppConfigEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(SetRoleAppStateEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(RoleGAgentState));
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

    [Fact]
    public void ProtoMessages_ShouldCoverGeneratedBranchesAndUnknownFields()
    {
        var request = new ChatRequestEvent
        {
            Prompt = "p",
            SessionId = "s",
            Metadata = { ["k"] = "v" },
        };
        request.MergeFrom(new ChatRequestEvent());
        request.MergeFrom((ChatRequestEvent)null!);
        request.Equals(request).Should().BeTrue();
        request.Equals((object?)null).Should().BeFalse();
        request!.GetHashCode().Should().NotBe(0);

        var response = new ChatResponseEvent { Content = "c", SessionId = "s" };
        response.MergeFrom((ChatResponseEvent)null!);
        response.Equals(response).Should().BeTrue();
        response.Equals((object?)null).Should().BeFalse();

        var textStart = new TextMessageStartEvent { SessionId = "s", AgentId = "a" };
        textStart.MergeFrom((TextMessageStartEvent)null!);
        textStart.Equals((object?)null).Should().BeFalse();

        var textContent = new TextMessageContentEvent { SessionId = "s", Delta = "d" };
        textContent.MergeFrom((TextMessageContentEvent)null!);
        textContent.Equals((object?)null).Should().BeFalse();

        var textEnd = new TextMessageEndEvent { SessionId = "s", Content = "e" };
        textEnd.MergeFrom((TextMessageEndEvent)null!);
        textEnd.Equals((object?)null).Should().BeFalse();

        var toolCall = new ToolCallEvent { ToolName = "tool", ArgumentsJson = "{}", CallId = "call-1" };
        toolCall.MergeFrom((ToolCallEvent)null!);
        toolCall.Equals((object?)null).Should().BeFalse();

        var toolResult = new ToolResultEvent { CallId = "call-1", ResultJson = "{}", Success = true, Error = "" };
        toolResult.MergeFrom((ToolResultEvent)null!);
        toolResult.Equals((object?)null).Should().BeFalse();

        var configure = new ConfigureRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            AppConfigJson = "{}",
            AppConfigCodec = RoleGAgentExtensionContract.AppConfigCodecJsonPlain,
            AppConfigSchemaVersion = 1,
        };
        configure.MergeFrom((ConfigureRoleAgentEvent)null!);
        configure.Equals((object?)null).Should().BeFalse();

        var appConfigEvent = new SetRoleAppConfigEvent
        {
            AppConfigJson = "{}",
            AppConfigCodec = RoleGAgentExtensionContract.AppConfigCodecJsonPlain,
            AppConfigSchemaVersion = 2,
        };
        appConfigEvent.MergeFrom((SetRoleAppConfigEvent)null!);
        appConfigEvent.Equals((object?)null).Should().BeFalse();

        var appStateEvent = new SetRoleAppStateEvent
        {
            AppState = Any.Pack(new ChatResponseEvent { Content = "payload" }),
            AppStateCodec = RoleGAgentExtensionContract.AppStateCodecProtobufAny,
            AppStateSchemaVersion = 1,
        };
        appStateEvent.MergeFrom((SetRoleAppStateEvent)null!);
        appStateEvent.Equals((object?)null).Should().BeFalse();

        var state = new RoleGAgentState
        {
            RoleName = "assistant",
            MessageCount = 1,
            AppConfigJson = "{}",
            AppConfigCodec = RoleGAgentExtensionContract.AppConfigCodecJsonPlain,
            AppConfigSchemaVersion = 1,
        };
        state.MergeFrom((RoleGAgentState)null!);
        state.Equals((object?)null).Should().BeFalse();

        var parsedResponse = ChatResponseEvent.Parser.ParseFrom(new byte[]
        {
            10, 1, (byte)'x', // content
            18, 1, (byte)'s', // session_id
            0x98, 0x06, 0x01, // unknown field 99 = 1
        });
        parsedResponse.Content.Should().Be("x");
        parsedResponse.SessionId.Should().Be("s");
        parsedResponse.ToByteArray().Length.Should().BeGreaterThan(4);

        var parsedToolResult = ToolResultEvent.Parser.ParseFrom(new byte[]
        {
            10, 2, (byte)'i', (byte)'d', // call_id
            24, 1,                       // success=true
            0x98, 0x06, 0x01,            // unknown field 99 = 1
        });
        parsedToolResult.CallId.Should().Be("id");
        parsedToolResult.Success.Should().BeTrue();
        parsedToolResult.ToByteArray().Length.Should().BeGreaterThan(3);
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
