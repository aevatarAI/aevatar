using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Integration.AI.Tests;

public sealed class WorkflowLlmStreamRunEventEnvelopeMappingHandlerTests
{
    [Fact]
    public void TryMap_ShouldMapTextDeltaToTextMessageContent()
    {
        var events = Map(new WorkflowLlmTextDeltaEvent
        {
            SessionId = "session-1",
            Delta = "hello",
        });

        events.Should().ContainSingle();
        events[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.TextMessageContent);
        events[0].TextMessageContent.MessageId.Should().Be("msg:session-1");
        events[0].TextMessageContent.Delta.Should().Be("hello");
    }

    [Fact]
    public void TryMap_ShouldMapReasoningDeltaToCustomReasoningEvent()
    {
        var events = Map(new WorkflowLlmReasoningDeltaEvent
        {
            SessionId = "session-2",
            Delta = "thinking",
            WorkerId = "critic",
        });

        events.Should().ContainSingle();
        events[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.Custom);
        events[0].Custom.Name.Should().Be("aevatar.llm.reasoning");
        var payload = events[0].Custom.Payload.Unpack<WorkflowReasoningCustomPayload>();
        payload.SessionId.Should().Be("session-2");
        payload.Delta.Should().Be("thinking");
        payload.Role.Should().Be("critic");
    }

    [Fact]
    public void TryMap_ShouldMapToolCallDeltaToToolCallStart()
    {
        var events = Map(new WorkflowLlmToolCallDeltaEvent
        {
            CallId = "call-1",
            ToolName = "search",
            ArgumentsJson = """{"q":"test"}""",
        });

        events.Should().ContainSingle();
        events[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.ToolCallStart);
        events[0].ToolCallStart.ToolCallId.Should().Be("call-1");
        events[0].ToolCallStart.ToolName.Should().Be("search");
    }

    [Fact]
    public void TryMap_ShouldMapToolResultToToolCallEnd()
    {
        var events = Map(new WorkflowLlmToolResultEvent
        {
            CallId = "call-2",
            ResultJson = """{"ok":true}""",
            Success = true,
        });

        events.Should().ContainSingle();
        events[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.ToolCallEnd);
        events[0].ToolCallEnd.ToolCallId.Should().Be("call-2");
        events[0].ToolCallEnd.Result.Should().Be("""{"ok":true}""");
    }

    [Fact]
    public void TryMap_ShouldMapInvocationCompletionToTextMessageEnd()
    {
        var events = Map(new WorkflowLlmInvocationCompletedEvent
        {
            SessionId = "session-3",
            Success = true,
            Content = "done",
        });

        events.Should().ContainSingle();
        events[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.TextMessageEnd);
        events[0].TextMessageEnd.MessageId.Should().Be("msg:session-3");
    }

    private static IReadOnlyList<WorkflowRunEventEnvelope> Map<T>(T evt) where T : IMessage
    {
        var handler = new WorkflowLlmStreamRunEventEnvelopeMappingHandler();
        var mapped = handler.TryMap(Envelope(evt), out var events);

        mapped.Should().BeTrue();
        return events;
    }

    private static EventEnvelope Envelope<T>(T evt) where T : IMessage =>
        new()
        {
            Id = "envelope-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("workflow:writer", TopologyAudience.Children),
        };
}
