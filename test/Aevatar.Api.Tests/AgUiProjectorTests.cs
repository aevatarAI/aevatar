// ─── AgUiProjector 投影测试 ───
// 验证 EventEnvelope → AG-UI 事件的映射正确性

using Aevatar.Presentation.AGUI;
using Aevatar.AI.Abstractions;
using Aevatar.Hosts.Api.Projection;
using Aevatar.Workflows.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Hosts.Api.Tests;

public class AgUiProjectorTests
{
    private static EventEnvelope Wrap<T>(T evt) where T : IMessage => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        PublisherId = "test",
        Direction = EventDirection.Down,
    };

    [Fact]
    public void StartWorkflowEvent_ProjectsTo_RunStarted()
    {
        var envelope = Wrap(new StartWorkflowEvent
        {
            WorkflowName = "review", RunId = "run-1", Input = "hello",
        });

        var events = AgUiProjector.Project(envelope);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<RunStartedEvent>();
        var e = (RunStartedEvent)events[0];
        e.ThreadId.Should().Be("review");
        e.RunId.Should().Be("run-1");
    }

    [Fact]
    public void StepRequestEvent_ProjectsTo_StepStartedAndCustom()
    {
        var envelope = Wrap(new StepRequestEvent
        {
            StepId = "analyze", StepType = "llm_call", RunId = "run-1",
        });

        var events = AgUiProjector.Project(envelope);

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
            StepId = "analyze", RunId = "run-1", Success = true, Output = "done",
        });

        var events = AgUiProjector.Project(envelope);

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

        var events = AgUiProjector.Project(envelope);

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

        var events = AgUiProjector.Project(envelope);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<Aevatar.Presentation.AGUI.TextMessageContentEvent>();
    }

    [Fact]
    public void WorkflowCompletedEvent_Success_ProjectsTo_RunFinished()
    {
        var envelope = Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "review", RunId = "run-1", Success = true, Output = "完成",
        });

        var events = AgUiProjector.Project(envelope);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<RunFinishedEvent>();
    }

    [Fact]
    public void WorkflowCompletedEvent_Failed_ProjectsTo_RunError()
    {
        var envelope = Wrap(new WorkflowCompletedEvent
        {
            WorkflowName = "review", RunId = "run-1", Success = false, Error = "超时",
        });

        var events = AgUiProjector.Project(envelope);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<RunErrorEvent>();
        ((RunErrorEvent)events[0]).Message.Should().Be("超时");
    }

    [Fact]
    public void ToolCallEvent_ProjectsTo_ToolCallStart()
    {
        var envelope = Wrap(new ToolCallEvent
        {
            ToolName = "search", CallId = "tc-1", ArgumentsJson = "{}",
        });

        var events = AgUiProjector.Project(envelope);

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

        var events = AgUiProjector.Project(envelope);

        events.Should().HaveCount(1);
        events[0].Should().BeOfType<ToolCallEndEvent>();
    }

    [Fact]
    public void UnknownPayload_ReturnsEmpty()
    {
        // ParentChangedEvent 没有投影规则
        var envelope = Wrap(new ParentChangedEvent { OldParent = "a", NewParent = "b" });

        var events = AgUiProjector.Project(envelope);

        events.Should().BeEmpty();
    }

    [Fact]
    public void NullPayload_ReturnsEmpty()
    {
        var envelope = new EventEnvelope
        {
            Id = "test", PublisherId = "x", Direction = EventDirection.Down,
        };

        var events = AgUiProjector.Project(envelope);

        events.Should().BeEmpty();
    }
}
