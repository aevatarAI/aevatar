using Aevatar.AI.Abstractions;
using Aevatar.AI.Projection.Appliers;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

public class AIProjectionApplierTests
{
    [Fact]
    public void TextMessageContentApplier_ShouldAppendTimelineWithDeltaLength()
    {
        var readModel = new TimelineReadModel();
        var applier = new AITextMessageContentProjectionApplier<TimelineReadModel, TestProjectionContext>();
        var envelope = Envelope(publisherId: "agent-1", typeUrl: "type://content");

        var applied = applier.Apply(
            readModel,
            new TestProjectionContext(),
            envelope,
            new TextMessageContentEvent
            {
                SessionId = "session-1",
                Delta = "hello",
            },
            now: new DateTimeOffset(2026, 2, 26, 0, 0, 0, TimeSpan.Zero));

        applied.Should().BeTrue();
        readModel.Timeline.Should().ContainSingle();
        var timeline = readModel.Timeline[0];
        timeline.Stage.Should().Be("llm.content");
        timeline.AgentId.Should().Be("agent-1");
        timeline.EventType.Should().Be("type://content");
        timeline.Data["session_id"].Should().Be("session-1");
        timeline.Data["delta_length"].Should().Be("5");
    }

    [Fact]
    public void TextMessageStartApplier_ShouldUseFallbackPublisherAndSession()
    {
        var readModel = new TimelineReadModel();
        var applier = new AITextMessageStartProjectionApplier<TimelineReadModel, TestProjectionContext>();
        var envelope = Envelope(publisherId: " ", typeUrl: "type://start");

        var applied = applier.Apply(
            readModel,
            new TestProjectionContext(),
            envelope,
            new TextMessageStartEvent
            {
                SessionId = "",
            },
            now: new DateTimeOffset(2026, 2, 26, 0, 0, 1, TimeSpan.Zero));

        applied.Should().BeTrue();
        readModel.Timeline.Should().ContainSingle();
        var timeline = readModel.Timeline[0];
        timeline.Stage.Should().Be("llm.start");
        timeline.AgentId.Should().Be("(unknown)");
        timeline.EventType.Should().Be("type://start");
        timeline.Data["session_id"].Should().Be("");
    }

    [Fact]
    public void ToolCallApplier_ShouldAppendToolMetadata()
    {
        var readModel = new TimelineReadModel();
        var applier = new AIToolCallProjectionApplier<TimelineReadModel, TestProjectionContext>();
        var envelope = Envelope(publisherId: "agent-2", typeUrl: "type://tool-call");

        var applied = applier.Apply(
            readModel,
            new TestProjectionContext(),
            envelope,
            new ToolCallEvent
            {
                ToolName = "search",
                CallId = "call-1",
            },
            now: new DateTimeOffset(2026, 2, 26, 0, 0, 2, TimeSpan.Zero));

        applied.Should().BeTrue();
        readModel.Timeline.Should().ContainSingle();
        var timeline = readModel.Timeline[0];
        timeline.Stage.Should().Be("tool.call");
        timeline.AgentId.Should().Be("agent-2");
        timeline.EventType.Should().Be("type://tool-call");
        timeline.Data["tool_name"].Should().Be("search");
        timeline.Data["call_id"].Should().Be("call-1");
    }

    [Fact]
    public void ToolResultApplier_ShouldAppendResultMetadataWithNullFallbacks()
    {
        var readModel = new TimelineReadModel();
        var applier = new AIToolResultProjectionApplier<TimelineReadModel, TestProjectionContext>();
        var envelope = Envelope(publisherId: "agent-3", typeUrl: "type://tool-result");

        var applied = applier.Apply(
            readModel,
            new TestProjectionContext(),
            envelope,
            new ToolResultEvent
            {
                CallId = "",
                Success = false,
                Error = "",
            },
            now: new DateTimeOffset(2026, 2, 26, 0, 0, 3, TimeSpan.Zero));

        applied.Should().BeTrue();
        readModel.Timeline.Should().ContainSingle();
        var timeline = readModel.Timeline[0];
        timeline.Stage.Should().Be("tool.result");
        timeline.AgentId.Should().Be("agent-3");
        timeline.EventType.Should().Be("type://tool-result");
        timeline.Data["call_id"].Should().Be("");
        timeline.Data["success"].Should().Be("False");
        timeline.Data["error"].Should().Be("");
    }

    private static EventEnvelope Envelope(string publisherId, string typeUrl)
    {
        return new EventEnvelope
        {
            Route = new EnvelopeRoute
            {
                PublisherActorId = publisherId,
            },
            Payload = new Any { TypeUrl = typeUrl },
        };
    }

    private sealed class TimelineReadModel : IHasProjectionTimeline
    {
        public List<ProjectionTimelineEvent> Timeline { get; } = [];

        public void AddTimeline(ProjectionTimelineEvent timelineEvent)
        {
            Timeline.Add(timelineEvent);
        }
    }

    private sealed class TestProjectionContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = "root-1";

        public string ProjectionKind { get; init; } = "ai-tests";
    }
}
