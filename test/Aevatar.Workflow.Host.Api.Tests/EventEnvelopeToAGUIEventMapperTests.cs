// ─── EventEnvelopeToAGUIEventMapper 投影测试 ───
// 验证 EventEnvelope → AG-UI 事件的映射正确性

using Aevatar.Presentation.AGUI;
using Aevatar.AI.Abstractions;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public class EventEnvelopeToAGUIEventMapperTests
{
    private static EventEnvelope Wrap<T>(T evt) where T : IMessage => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        PublisherId = "test",
        CorrelationId = "cmd-1",
        Direction = EventDirection.Down,
    };

    [Fact]
    public void StartWorkflowEvent_ProjectsTo_RunStarted()
    {
        var envelope = Wrap(new StartWorkflowEvent
        {
            WorkflowName = "review", Input = "hello",
        });

        var events = CreateMapper().Map(envelope);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<RunStartedEvent>();
        var e = (RunStartedEvent)events[0];
        e.ThreadId.Should().Be("test");
        e.RunId.Should().Be("cmd-1");
    }

    [Fact]
    public void StepRequestEvent_ProjectsTo_StepStartedAndCustom()
    {
        var envelope = Wrap(new StepRequestEvent
        {
            StepId = "analyze", StepType = "llm_call",
        });

        var events = CreateMapper().Map(envelope);

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<StepStartedEvent>();
        ((StepStartedEvent)events[0]).StepName.Should().Be("analyze");
        events[1].Should().BeOfType<CustomEvent>();
    }

    [Fact]
    public void StepCompletedEvent_ProjectsTo_StepFinished()
    {
        var envelope = Wrap(new StepCompletedEvent
        {
            StepId = "analyze", Success = true, Output = "done",
        });

        var events = CreateMapper().Map(envelope);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<StepFinishedEvent>();
        ((StepFinishedEvent)events[0]).StepName.Should().Be("analyze");
    }

    [Fact]
    public void ChatResponseEvent_ProjectsTo_FullTextMessageSequence()
    {
        var envelope = Wrap(new ChatResponseEvent
        {
            Content = "分析结果如下...", SessionId = "s1",
        });

        var events = CreateMapper().Map(envelope);

        events.Should().HaveCount(3);
        events[0].Should().BeOfType<Aevatar.Presentation.AGUI.TextMessageStartEvent>();
        events[1].Should().BeOfType<Aevatar.Presentation.AGUI.TextMessageContentEvent>();
        events[2].Should().BeOfType<Aevatar.Presentation.AGUI.TextMessageEndEvent>();

        var content = (Aevatar.Presentation.AGUI.TextMessageContentEvent)events[1];
        content.Delta.Should().Be("分析结果如下...");
    }

    [Fact]
    public void TextMessageContentEvent_ProjectsTo_AguiContent()
    {
        var envelope = Wrap(new Aevatar.AI.Abstractions.TextMessageContentEvent
        {
            Delta = "部分", SessionId = "s1",
        });

        var events = CreateMapper().Map(envelope);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<Aevatar.Presentation.AGUI.TextMessageContentEvent>();
    }

    [Fact]
    public void TextMessageStartAndEnd_ProjectsTo_AguiEvents()
    {
        var startEnvelope = Wrap(new Aevatar.AI.Abstractions.TextMessageStartEvent
        {
            SessionId = "s2",
            AgentId = "agent-1",
        });
        var endEnvelope = Wrap(new Aevatar.AI.Abstractions.TextMessageEndEvent
        {
            SessionId = "s2",
            Content = "done",
        });

        var start = CreateMapper().Map(startEnvelope);
        var end = CreateMapper().Map(endEnvelope);

        start.Should().ContainSingle().Which.Should().BeOfType<Aevatar.Presentation.AGUI.TextMessageStartEvent>();
        ((Aevatar.Presentation.AGUI.TextMessageStartEvent)start[0]).MessageId.Should().Be("msg:s2");
        end.Should().ContainSingle().Which.Should().BeOfType<Aevatar.Presentation.AGUI.TextMessageEndEvent>();
        ((Aevatar.Presentation.AGUI.TextMessageEndEvent)end[0]).MessageId.Should().Be("msg:s2");
    }

    [Fact]
    public void TextMessageContentEvent_WithoutSessionId_ShouldFallbackToEnvelopeIdMessageId()
    {
        var envelope = Wrap(new Aevatar.AI.Abstractions.TextMessageContentEvent
        {
            Delta = "fallback",
            SessionId = "",
        });
        envelope.Id = "env-fallback";
        envelope.Timestamp = null;

        var events = CreateMapper().Map(envelope);

        var content = events.Should().ContainSingle().Which.Should().BeOfType<Aevatar.Presentation.AGUI.TextMessageContentEvent>().Subject;
        content.MessageId.Should().Be("msg:env-fallback");
        content.Timestamp.Should().BeNull();
    }

    [Fact]
    public void WorkflowCompletedEvent_Success_ProjectsTo_RunFinished()
    {
        var envelope = Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "review", Success = true, Output = "完成",
        });

        var events = CreateMapper().Map(envelope);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<RunFinishedEvent>();
        var runFinished = (RunFinishedEvent)events[0];
        runFinished.ThreadId.Should().Be("test");
        runFinished.RunId.Should().Be("cmd-1");
    }

    [Fact]
    public void WorkflowCompletedEvent_Failed_ProjectsTo_RunError()
    {
        var envelope = Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "review", Success = false, Error = "超时",
        });

        var events = CreateMapper().Map(envelope);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<RunErrorEvent>();
        var runError = (RunErrorEvent)events[0];
        runError.Message.Should().Be("超时");
        runError.RunId.Should().Be("cmd-1");
    }

    [Fact]
    public void WorkflowCompletedEvent_WhenPublisherAndCorrelationMissing_ShouldFallbackRunAndThread()
    {
        var envelope = Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "review",
            Success = true,
            Output = "ok",
        });
        envelope.PublisherId = "";
        envelope.CorrelationId = "";

        var events = CreateMapper().Map(envelope);

        var finished = events.Should().ContainSingle().Which.Should().BeOfType<RunFinishedEvent>().Subject;
        finished.ThreadId.Should().Be("review");
        finished.RunId.Should().Be("review");
    }

    [Fact]
    public void StartWorkflowEvent_WithoutCorrelationId_RunIdFallsBackToThread()
    {
        var envelope = Wrap(new StartWorkflowEvent
        {
            WorkflowName = "review", Input = "hello",
        });
        envelope.CorrelationId = "";

        var events = CreateMapper().Map(envelope);

        events.Should().ContainSingle().Which.Should().BeOfType<RunStartedEvent>();
        var runStarted = (RunStartedEvent)events[0];
        runStarted.ThreadId.Should().Be("test");
        runStarted.RunId.Should().Be("test");
    }

    [Fact]
    public void ToolCallEvent_ProjectsTo_ToolCallStart()
    {
        var envelope = Wrap(new ToolCallEvent
        {
            ToolName = "search", CallId = "tc-1", ArgumentsJson = "{}",
        });

        var events = CreateMapper().Map(envelope);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<ToolCallStartEvent>();
    }

    [Fact]
    public void ToolResultEvent_ProjectsTo_ToolCallEnd()
    {
        var envelope = Wrap(new ToolResultEvent
        {
            CallId = "tc-1", ResultJson = "{\"found\":true}", Success = true,
        });

        var events = CreateMapper().Map(envelope);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<ToolCallEndEvent>();
    }

    [Fact]
    public void UnknownPayload_ReturnsEmpty()
    {
        // ParentChangedEvent 没有投影规则
        var envelope = Wrap(new ParentChangedEvent { OldParent = "a", NewParent = "b" });

        var events = CreateMapper().Map(envelope);

        events.Should().BeEmpty();
    }

    [Fact]
    public void NullPayload_ReturnsEmpty()
    {
        var envelope = new EventEnvelope
        {
            Id = "test", PublisherId = "x", Direction = EventDirection.Down,
        };

        var events = CreateMapper().Map(envelope);

        events.Should().BeEmpty();
    }

    private static IEventEnvelopeToAGUIEventMapper CreateMapper()
    {
        return new EventEnvelopeToAGUIEventMapper(
        [
            new StartWorkflowAGUIEventEnvelopeMappingHandler(),
            new StepRequestAGUIEventEnvelopeMappingHandler(),
            new StepCompletedAGUIEventEnvelopeMappingHandler(),
            new AITextStreamAGUIEventEnvelopeMappingHandler(),
            new WorkflowCompletedAGUIEventEnvelopeMappingHandler(),
            new ToolCallAGUIEventEnvelopeMappingHandler(),
        ]);
    }
}
