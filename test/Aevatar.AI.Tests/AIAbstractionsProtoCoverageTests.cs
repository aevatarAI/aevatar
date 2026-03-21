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
            TimeoutMs = 2500,
            ScopeId = "scope-1",
            InputParts =
            {
                new ChatContentPart
                {
                    Kind = ChatContentPartKind.Image,
                    Uri = "https://example.com/cat.png",
                    MediaType = "image/png",
                    Name = "cat",
                },
            },
        }, ChatRequestEvent.Parser);
        request.Metadata["correlation_id"].Should().Be("c-1");
        request.TimeoutMs.Should().Be(2500);
        request.ScopeId.Should().Be("scope-1");
        request.InputParts.Should().ContainSingle();
        request.InputParts[0].Kind.Should().Be(ChatContentPartKind.Image);

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

        var textReasoning = RoundTrip(new TextMessageReasoningEvent
        {
            SessionId = "session-1",
            Delta = "reasoning-delta",
        }, TextMessageReasoningEvent.Parser);
        textReasoning.Delta.Should().Be("reasoning-delta");

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

        var sessionStarted = RoundTrip(new RoleChatSessionStartedEvent
        {
            SessionId = "session-1",
            Prompt = "hello",
            InputParts =
            {
                new ChatContentPart
                {
                    Kind = ChatContentPartKind.Text,
                    Text = "hello",
                },
            },
        }, RoleChatSessionStartedEvent.Parser);
        sessionStarted.Prompt.Should().Be("hello");
        sessionStarted.InputParts.Should().ContainSingle();

        var sessionCompleted = RoundTrip(new RoleChatSessionCompletedEvent
        {
            SessionId = "session-1",
            Content = "done",
            ReasoningContent = "thinking",
            Prompt = "hello",
            ContentEmitted = true,
            ToolCalls =
            {
                new ToolCallEvent
                {
                    ToolName = "search",
                    ArgumentsJson = "{\"q\":\"x\"}",
                    CallId = "call-1",
                },
            },
            OutputParts =
            {
                new ChatContentPart
                {
                    Kind = ChatContentPartKind.Image,
                    Uri = "https://example.com/output.png",
                    MediaType = "image/png",
                },
            },
        }, RoleChatSessionCompletedEvent.Parser);
        sessionCompleted.Content.Should().Be("done");
        sessionCompleted.ReasoningContent.Should().Be("thinking");
        sessionCompleted.ContentEmitted.Should().BeTrue();
        sessionCompleted.ToolCalls.Should().ContainSingle();
        sessionCompleted.OutputParts.Should().ContainSingle();

        var initialize = RoundTrip(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "system",
            Temperature = 0.3,
            MaxTokens = 120,
            MaxToolRounds = 3,
            MaxHistoryMessages = 40,
            StreamBufferCapacity = 128,
            EventModules = "demo",
            EventRoutes = "event.type == X -> demo",
        }, InitializeRoleAgentEvent.Parser);
        initialize.RoleName.Should().Be("assistant");
        initialize.HasTemperature.Should().BeTrue();

        var overrides = RoundTrip(new AIAgentConfigOverrides
        {
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "system",
            Temperature = 0.4,
            MaxTokens = 128,
            MaxToolRounds = 2,
            MaxHistoryMessages = 16,
            StreamBufferCapacity = 64,
        }, AIAgentConfigOverrides.Parser);
        overrides.ProviderName.Should().Be("mock");

        var state = RoundTrip(new RoleGAgentState
        {
            RoleName = "assistant",
            MessageCount = 7,
            ConfigOverrides = overrides,
            EventModules = "demo",
            EventRoutes = "event.type == X -> demo",
            Sessions =
            {
                ["session-1"] = new RoleChatSessionState
                {
                    Prompt = "hello",
                    Completed = true,
                    FinalContent = "done",
                    FinalReasoningContent = "thinking",
                    Sequence = 7,
                    ContentEmitted = true,
                    InputParts =
                    {
                        new ChatContentPart
                        {
                            Kind = ChatContentPartKind.Text,
                            Text = "hello",
                        },
                    },
                    OutputParts =
                    {
                        new ChatContentPart
                        {
                            Kind = ChatContentPartKind.Image,
                            Uri = "https://example.com/output.png",
                            MediaType = "image/png",
                        },
                    },
                    ToolCalls =
                    {
                        new ToolCallEvent
                        {
                            ToolName = "search",
                            ArgumentsJson = "{\"q\":\"x\"}",
                            CallId = "call-1",
                        },
                    },
                },
            },
        }, RoleGAgentState.Parser);
        state.RoleName.Should().Be("assistant");
        state.MessageCount.Should().Be(7);
        state.EventModules.Should().Be("demo");
        state.EventRoutes.Should().Be("event.type == X -> demo");
        state.Sessions["session-1"].FinalContent.Should().Be("done");
        state.Sessions["session-1"].Sequence.Should().Be(7);
        state.Sessions["session-1"].InputParts.Should().ContainSingle();
        state.Sessions["session-1"].OutputParts.Should().ContainSingle();
        state.Sessions["session-1"].ToolCalls.Should().ContainSingle();
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
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(TextMessageReasoningEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(TextMessageEndEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ToolCallEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(ToolResultEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(RoleChatSessionStartedEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(RoleChatSessionCompletedEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(InitializeRoleAgentEvent));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(AIAgentConfigOverrides));
        AiMessagesReflection.Descriptor.MessageTypes.Should().Contain(x => x.Name == nameof(RoleChatSessionState));
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
        var sessionStarted = new RoleChatSessionStartedEvent();
        var initialize = new InitializeRoleAgentEvent();
        var state = new RoleGAgentState();

        Action setRequestPrompt = () => request.Prompt = null!;
        Action setResponseContent = () => response.Content = null!;
        Action setTextStartSession = () => textStart.SessionId = null!;
        Action setToolCallName = () => toolCall.ToolName = null!;
        Action setToolResultCallId = () => toolResult.CallId = null!;
        Action setSessionStartedId = () => sessionStarted.SessionId = null!;
        Action setInitRoleName = () => initialize.RoleName = null!;
        Action setStateRoleName = () => state.RoleName = null!;

        setRequestPrompt.Should().Throw<ArgumentNullException>();
        setResponseContent.Should().Throw<ArgumentNullException>();
        setTextStartSession.Should().Throw<ArgumentNullException>();
        setToolCallName.Should().Throw<ArgumentNullException>();
        setToolResultCallId.Should().Throw<ArgumentNullException>();
        setSessionStartedId.Should().Throw<ArgumentNullException>();
        setInitRoleName.Should().Throw<ArgumentNullException>();
        setStateRoleName.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProtoMessages_ShouldCoverGeneratedBranchesAndUnknownFields()
    {
        var initialize = new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "system",
            MaxTokens = 128,
        };
        initialize.MergeFrom((InitializeRoleAgentEvent)null!);
        initialize.Equals((object?)null).Should().BeFalse();

        var overrides = new AIAgentConfigOverrides
        {
            ProviderName = "mock",
            Model = "m1",
            SystemPrompt = "system",
            MaxTokens = 128,
        };
        overrides.MergeFrom((AIAgentConfigOverrides)null!);
        overrides.Equals((object?)null).Should().BeFalse();

        var state = new RoleGAgentState
        {
            RoleName = "assistant",
            MessageCount = 1,
            ConfigOverrides = overrides,
            EventModules = "demo",
            EventRoutes = "event.type == X -> demo",
            Sessions =
            {
                ["session-1"] = new RoleChatSessionState
                {
                    Prompt = "hello",
                    Completed = true,
                    FinalContent = "done",
                    FinalReasoningContent = "thinking",
                    Sequence = 1,
                    ContentEmitted = true,
                    ToolCalls =
                    {
                        new ToolCallEvent
                        {
                            ToolName = "search",
                            ArgumentsJson = "{\"q\":\"x\"}",
                            CallId = "call-1",
                        },
                    },
                },
            },
        };
        state.MergeFrom((RoleGAgentState)null!);
        state.Equals((object?)null).Should().BeFalse();

        var parsedResponse = ChatResponseEvent.Parser.ParseFrom(new byte[]
        {
            10, 1, (byte)'x',
            18, 1, (byte)'s',
            0x98, 0x06, 0x01,
        });
        parsedResponse.Content.Should().Be("x");
        parsedResponse.SessionId.Should().Be("s");
        parsedResponse.ToByteArray().Length.Should().BeGreaterThan(4);
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
