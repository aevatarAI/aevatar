using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.AGUI.Contracts;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using AiTextContent = Aevatar.AI.Abstractions.TextMessageContentEvent;
using AiTextEnd = Aevatar.AI.Abstractions.TextMessageEndEvent;
using AiTextReasoning = Aevatar.AI.Abstractions.TextMessageReasoningEvent;
using AiTextStart = Aevatar.AI.Abstractions.TextMessageStartEvent;
using AiToolCall = Aevatar.AI.Abstractions.ToolCallEvent;
using AiToolResult = Aevatar.AI.Abstractions.ToolResultEvent;

namespace Aevatar.GAgentService.Tests;

// Refactor (iter5/cluster-010):
//   Old: Integration tests reached into ScopeGAgentEndpoints mapper wrappers through reflection.
//   New: mapper behavior is verified directly at the ScopeGAgentAguiEventMapper boundary.
public sealed class ScopeGAgentAguiEventMapperTests
{
    [Fact]
    public void TryMap_ShouldMapAIAndToolingEvents()
    {
        var textStart = ScopeGAgentAguiEventMapper.TryMap(
            BuildEventEnvelope(new AiTextStart { SessionId = "s1", AgentId = "agent-1" }));
        textStart!.TextMessageStart.Should().NotBeNull();
        textStart.TextMessageStart!.MessageId.Should().Be("s1");

        var textContent = ScopeGAgentAguiEventMapper.TryMap(
            BuildEventEnvelope(new AiTextContent { Delta = "d", SessionId = "s1" }));
        textContent!.TextMessageContent.Should().NotBeNull();
        textContent.TextMessageContent!.Delta.Should().Be("d");

        var reasoning = ScopeGAgentAguiEventMapper.TryMap(
            BuildEventEnvelope(new AiTextReasoning { Delta = "r", SessionId = "s1" }));
        reasoning!.Custom.Should().NotBeNull();
        reasoning.Custom!.Name.Should().Be("TEXT_MESSAGE_REASONING");

        var textEnd = ScopeGAgentAguiEventMapper.TryMap(
            BuildEventEnvelope(new AiTextEnd { Content = "done", SessionId = "s1" }));
        textEnd!.TextMessageEnd.Should().NotBeNull();

        var textEndError = ScopeGAgentAguiEventMapper.TryMap(
            BuildEventEnvelope(new AiTextEnd { Content = "[[AEVATAR_LLM_ERROR]] boom", SessionId = "s2" }));
        textEndError!.RunError.Should().NotBeNull();
        textEndError.RunError!.Message.Should().Be("boom");

        var textEndFailed = ScopeGAgentAguiEventMapper.TryMap(
            BuildEventEnvelope(new AiTextEnd { Content = "LLM request failed: upstream", SessionId = "s2" }));
        textEndFailed!.RunError.Should().NotBeNull();
        textEndFailed.RunError!.Message.Should().Be("LLM request failed: upstream");

        var textEndToolFailed = ScopeGAgentAguiEventMapper.TryMap(
            BuildEventEnvelope(new AiTextEnd
            {
                Content = "LLM request failed [tools=none]: upstream",
                SessionId = "s2",
            }));
        textEndToolFailed!.RunError.Should().NotBeNull();
        textEndToolFailed.RunError!.Message.Should().Be("upstream");

        var toolCall = ScopeGAgentAguiEventMapper.TryMap(BuildEventEnvelope(new AiToolCall
        {
            ToolName = "search",
            CallId = "call-1",
        }));
        toolCall!.ToolCallStart.Should().NotBeNull();

        var toolResult = ScopeGAgentAguiEventMapper.TryMap(BuildEventEnvelope(new AiToolResult
        {
            CallId = "call-1",
            ResultJson = "{\"ok\":true}",
        }));
        toolResult!.ToolCallEnd.Should().NotBeNull();

        var approval = ScopeGAgentAguiEventMapper.TryMap(BuildEventEnvelope(new ToolApprovalRequestEvent
        {
            RequestId = "req-1",
            SessionId = "s1",
            ToolName = "connector.run",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
            IsDestructive = true,
            TimeoutSeconds = 30,
        }));
        approval.Should().NotBeNull();
        approval!.Custom.Should().NotBeNull();
        approval.Custom!.Name.Should().Be("TOOL_APPROVAL_REQUEST");
        approval.Custom.Payload.Should().NotBeNull();
        var approvalStruct = approval.Custom.Payload!.Unpack<Struct>();
        approvalStruct.Fields["toolName"].StringValue.Should().Be("connector.run");
        approvalStruct.Fields["isDestructive"].BoolValue.Should().BeTrue();
        approvalStruct.Fields["timeoutSeconds"].NumberValue.Should().Be(30);

        var usage = ScopeGAgentAguiEventMapper.TryMap(BuildEventEnvelope(new ChatTokenUsageEvent
        {
            SessionId = "s1",
            Usage = new TokenUsagePayload
            {
                PromptTokens = 5,
                CompletionTokens = 7,
                TotalTokens = 12,
            },
            Model = "nyxid-model",
        }));
        usage.Should().NotBeNull();
        usage!.Usage.Should().NotBeNull();
        usage.Usage.Available.Should().BeTrue();
        usage.Usage.PromptTokens.Should().Be(5);
        usage.Usage.CompletionTokens.Should().Be(7);
        usage.Usage.TotalTokens.Should().Be(12);
        usage.Usage.Model.Should().Be("nyxid-model");

        var agui = ScopeGAgentAguiEventMapper.TryMap(BuildEventEnvelope(new AGUIEvent
        {
            TextMessageEnd = new Aevatar.AGUI.Contracts.TextMessageEndEvent { MessageId = "m2" },
        }));
        agui.Should().NotBeNull();
        agui!.TextMessageEnd.Should().NotBeNull();

        var none = ScopeGAgentAguiEventMapper.TryMap(new EventEnvelope());
        none.Should().BeNull();
    }

    [Fact]
    public void TryMap_ShouldMapEmptyCompletionContent_ToTextMessageEnd()
    {
        var textEnd = ScopeGAgentAguiEventMapper.TryMap(
            BuildEventEnvelope(new AiTextEnd { Content = string.Empty, SessionId = "s-empty" }));

        textEnd.Should().NotBeNull();
        textEnd!.TextMessageEnd.Should().NotBeNull();
        textEnd.TextMessageEnd!.MessageId.Should().Be("s-empty");
        textEnd.RunError.Should().BeNull();
    }

    [Fact]
    public void TryMapExplicitSessionObservation_ShouldOnlyMapWrappedAguiEvent()
    {
        ScopeGAgentAguiEventMapper.TryMapExplicitSessionObservation(
            BuildEventEnvelope(new AiTextContent { Delta = "d", SessionId = "s1" }))
            .Should()
            .BeNull();

        var wrapped = new AGUIEvent
        {
            TextMessageContent = new Aevatar.AGUI.Contracts.TextMessageContentEvent
            {
                MessageId = "m1",
                Delta = "hello",
            },
        };

        ScopeGAgentAguiEventMapper.TryMapExplicitSessionObservation(BuildEventEnvelope(wrapped))
            .Should()
            .BeEquivalentTo(wrapped);
    }

    [Fact]
    public void TryMap_ShouldHandleUnknownPayloadAndWrappedAguiEvent()
    {
        ScopeGAgentAguiEventMapper.TryMap(new EventEnvelope
        {
            Payload = Any.Pack(new StringValue { Value = "unknown" }),
        }).Should().BeNull();

        var wrapped = new AGUIEvent
        {
            RunFinished = new RunFinishedEvent
            {
                ThreadId = "thread-1",
                RunId = "run-1",
            },
        };

        ScopeGAgentAguiEventMapper.TryMap(new EventEnvelope
        {
            Payload = Any.Pack(wrapped),
        }).Should().BeEquivalentTo(wrapped);
    }

    [Theory]
    [InlineData("", "LLM request failed.")]
    [InlineData("   ", "LLM request failed.")]
    [InlineData(" LLM request failed: upstream ", "LLM request failed: upstream")]
    [InlineData("LLM request failed [tools=none] upstream", "LLM request failed [tools=none] upstream")]
    [InlineData("LLM request failed [tools=none]:    ", "LLM request failed [tools=none]:")]
    [InlineData("LLM request failed [tools=chrono_grep,chrono_glob]: NyxID authentication required", "NyxID authentication required")]
    public void NormalizeLlmFailureMessage_ShouldNormalizeToolAnnotatedFailures(string content, string expected)
    {
        ScopeGAgentAguiEventMapper.NormalizeLlmFailureMessage(content).Should().Be(expected);
    }

    [Fact]
    public void BuildToolApprovalStruct_ShouldHandleDecodeFailure()
    {
        var invalidAny = new Any
        {
            TypeUrl = "type.googleapis.com/aevatar.ai.ToolApprovalRequestEvent",
            Value = ByteString.CopyFromUtf8("broken"),
        };

        var structure = ScopeGAgentAguiEventMapper.BuildToolApprovalStruct(invalidAny);
        structure.Fields.Should().ContainKey("error");
        structure.Fields["error"].StringValue.Should().Contain("Failed to decode approval request");
    }

    private static EventEnvelope BuildEventEnvelope(IMessage message)
    {
        return new EventEnvelope { Payload = Any.Pack(message) };
    }
}
