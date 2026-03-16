using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Projection.ReadModels;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowProjectionReadModelCoverageTests
{
    [Fact]
    public void WorkflowExecutionRuntimeLease_ShouldValidateContext_AndExposeProjectionIdentity()
    {
        Action missingContext = () => new WorkflowExecutionRuntimeLease(null!);

        missingContext.Should().Throw<ArgumentNullException>().WithParameterName("context");

        var lease = new WorkflowExecutionRuntimeLease(new WorkflowExecutionProjectionContext
        {
            RootActorId = "actor-1",
            SessionId = "session-1",
            ProjectionKind = "workflow-execution",
        });

        lease.RootEntityId.Should().Be("actor-1");
        lease.ActorId.Should().Be("actor-1");
        lease.ScopeId.Should().Be("actor-1");
        lease.CommandId.Should().Be("session-1");
        lease.SessionId.Should().Be("session-1");
        lease.Context.ProjectionKind.Should().Be("workflow-execution");
    }

    [Fact]
    public void WorkflowBindingSessionEventCodec_ShouldCoverEventTypeSerializeAndDeserializeBranches()
    {
        var codec = new WorkflowBindingSessionEventCodec();
        var envelope = new EventEnvelope
        {
            Id = "evt-1",
            Payload = Any.Pack(new StringValue { Value = "hello" }),
        };

        Action nullEventType = () => codec.GetEventType(null!);
        Action nullSerialize = () => codec.Serialize(null!);

        nullEventType.Should().Throw<ArgumentNullException>().WithParameterName("evt");
        nullSerialize.Should().Throw<ArgumentNullException>().WithParameterName("evt");

        codec.Channel.Should().Be("workflow-binding");
        codec.GetEventType(new EventEnvelope()).Should().Be(EventEnvelope.Descriptor.FullName);
        codec.GetEventType(envelope).Should().Be(envelope.Payload.TypeUrl);
        codec.Serialize(envelope).Should().BeEquivalentTo(envelope.ToByteString());

        codec.Deserialize("", ByteString.CopyFromUtf8("ignored")).Should().BeNull();
        codec.Deserialize(EventEnvelope.Descriptor.FullName, null!).Should().BeNull();
        codec.Deserialize(EventEnvelope.Descriptor.FullName, ByteString.Empty).Should().BeNull();
        codec.Deserialize("type.googleapis.com/google.protobuf.StringValue", ByteString.CopyFromUtf8("broken")).Should().BeNull();
        codec.Deserialize("type.googleapis.com/google.protobuf.Empty", envelope.ToByteString()).Should().BeNull();

        var decoded = codec.Deserialize(envelope.Payload.TypeUrl, envelope.ToByteString());
        decoded.Should().NotBeNull();
        decoded!.Id.Should().Be("evt-1");
        decoded.Payload.TypeUrl.Should().Be(envelope.Payload.TypeUrl);
    }

    [Fact]
    public void WorkflowRunInsightReportDocument_ShouldNormalizeCollectionsTimestampsAndSummary()
    {
        var localTime = new DateTimeOffset(2026, 3, 17, 21, 15, 0, TimeSpan.FromHours(8));
        var report = new WorkflowRunInsightReportDocument
        {
            RootActorId = "actor-1",
        };

        report.ActorId.Should().Be("actor-1");
        report.CreatedAt.Should().Be(default);
        report.UpdatedAt.Should().Be(default);
        report.ProjectionScope.Should().Be(WorkflowExecutionProjectionScope.ActorShared);
        report.TopologySource.Should().Be(WorkflowExecutionTopologySource.RuntimeSnapshot);
        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Running);

        report.CreatedAt = localTime;
        report.UpdatedAt = localTime;
        report.StartedAt = localTime;
        report.EndedAt = localTime;
        report.ProjectionScope = WorkflowExecutionProjectionScope.RunIsolated;
        report.CompletionStatus = WorkflowExecutionCompletionStatus.WaitingForSignal;
        report.Success = true;
        report.Topology =
        [
            new WorkflowExecutionTopologyEdge("step-1", "step-2"),
        ];
        report.Steps =
        [
            new WorkflowExecutionStepTrace
            {
                StepId = "step-1",
            },
        ];
        report.Timeline =
        [
            new WorkflowExecutionTimelineEvent
            {
                Stage = "existing",
            },
        ];
        report.RoleReplies =
        [
            new WorkflowExecutionRoleReply
            {
                RoleId = "role-1",
            },
        ];
        report.Summary = null!;
        report.Summary.StepTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["llm_call"] = 2,
        };

        report.AddTimeline(new ProjectionTimelineEvent
        {
            Timestamp = localTime,
            Stage = "workflow.completed",
            Message = "done",
            AgentId = "agent-1",
            StepId = "step-2",
            StepType = "llm_call",
            EventType = "completed",
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["result"] = "ok",
            },
        });
        report.AddRoleReply(new ProjectionRoleReply
        {
            Timestamp = localTime,
            RoleId = "role-2",
            SessionId = "session-1",
            Content = "answer",
            ContentLength = 6,
        });
        var clone = report.DeepClone();

        report.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
        report.UpdatedAt.Offset.Should().Be(TimeSpan.Zero);
        report.StartedAt.Offset.Should().Be(TimeSpan.Zero);
        report.EndedAt.Offset.Should().Be(TimeSpan.Zero);
        report.Success.Should().BeTrue();
        report.Topology.Should().ContainSingle();
        report.Steps.Should().ContainSingle();
        report.RoleReplies.Should().HaveCount(2);
        report.Timeline.Should().HaveCount(2);
        report.Timeline.Last().Data.Should().Contain("result", "ok");
        report.Summary.StepTypeCounts.Should().Contain("llm_call", 2);
        clone.Should().NotBeSameAs(report);
        clone.RoleReplies.Should().HaveCount(2);
        clone.Timeline.Should().HaveCount(2);

        report.Topology = null!;
        report.Steps = null!;
        report.RoleReplies = null!;
        report.Timeline = null!;
        report.Success = null;

        report.Topology.Should().BeEmpty();
        report.Steps.Should().BeEmpty();
        report.RoleReplies.Should().BeEmpty();
        report.Timeline.Should().BeEmpty();
        report.Success.Should().BeNull();
    }

    [Fact]
    public void WorkflowReadModels_ShouldCoverOptionalFieldsAndClonePaths()
    {
        var utcTime = DateTimeOffset.Parse("2026-03-17T12:00:00+00:00");

        var currentState = new WorkflowExecutionCurrentStateDocument
        {
            RootActorId = "actor-2",
        };
        currentState.ActorId.Should().Be("actor-2");
        currentState.UpdatedAt.Should().Be(default);
        currentState.Success.Should().BeNull();
        currentState.UpdatedAt = utcTime;
        currentState.Success = false;
        currentState.DeepClone().Success.Should().BeFalse();

        var timeline = new WorkflowRunTimelineDocument
        {
            RootActorId = "actor-2",
        };
        timeline.ActorId.Should().Be("actor-2");
        timeline.UpdatedAt.Should().Be(default);
        timeline.Timeline =
        [
            new WorkflowExecutionTimelineEvent
            {
                Timestamp = utcTime,
                Stage = "middle",
                Data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["k"] = "v",
                },
            },
        ];
        timeline.UpdatedAt = utcTime;
        timeline.DeepClone().Timeline.Should().ContainSingle();
        timeline.Timeline = null!;
        timeline.Timeline.Should().BeEmpty();

        var graph = new WorkflowRunGraphArtifactDocument
        {
            RootActorId = "actor-2",
        };
        graph.ActorId.Should().Be("actor-2");
        graph.UpdatedAt.Should().Be(default);
        graph.Topology =
        [
            new WorkflowExecutionTopologyEdge("a", "b"),
        ];
        graph.Steps =
        [
            new WorkflowExecutionStepTrace
            {
                StepId = "step-1",
            },
        ];
        graph.UpdatedAt = utcTime;
        graph.DeepClone().Topology.Should().ContainSingle();
        graph.Topology = null!;
        graph.Steps = null!;
        graph.Topology.Should().BeEmpty();
        graph.Steps.Should().BeEmpty();
    }

    [Fact]
    public void WorkflowExecutionStepTrace_And_RelatedModels_ShouldCoverTimestampMapAndDurationBranches()
    {
        var requestedAt = DateTimeOffset.Parse("2026-03-17T12:00:00+00:00");
        var completedAt = requestedAt.AddSeconds(2);
        var beforeRequest = requestedAt.AddSeconds(-3);

        var step = new WorkflowExecutionStepTrace();
        step.RequestedAt.Should().BeNull();
        step.CompletedAt.Should().BeNull();
        step.Success.Should().BeNull();
        step.SuspensionTimeoutSeconds.Should().BeNull();
        step.DurationMs.Should().BeNull();

        step.RequestedAt = requestedAt;
        step.CompletedAt = beforeRequest;
        step.Success = true;
        step.RequestParameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["temperature"] = "0.2",
        };
        step.CompletionAnnotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["token_usage"] = "99",
        };
        step.SuspensionTimeoutSeconds = 30;

        step.DurationMs.Should().Be(0);
        step.Success.Should().BeTrue();
        step.RequestParameters.Should().Contain("temperature", "0.2");
        step.CompletionAnnotations.Should().Contain("token_usage", "99");
        step.SuspensionTimeoutSeconds.Should().Be(30);

        step.CompletedAt = completedAt;
        step.DurationMs.Should().Be(2000);
        step.RequestParameters = null!;
        step.CompletionAnnotations = null!;
        step.SuspensionTimeoutSeconds = null;
        step.Success = null;

        step.RequestParameters.Should().BeEmpty();
        step.CompletionAnnotations.Should().BeEmpty();
        step.SuspensionTimeoutSeconds.Should().BeNull();
        step.Success.Should().BeNull();

        var roleReply = new WorkflowExecutionRoleReply
        {
            Timestamp = requestedAt,
            RoleId = "role-1",
        };
        roleReply.Timestamp.Should().Be(requestedAt);

        var timelineEvent = new WorkflowExecutionTimelineEvent
        {
            Timestamp = completedAt,
            Stage = "stage-1",
        };
        timelineEvent.Timestamp.Should().Be(completedAt);
        timelineEvent.Data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = "ok",
        };
        timelineEvent.Data.Should().Contain("status", "ok");
        timelineEvent.Data = null!;
        timelineEvent.Data.Should().BeEmpty();

        var summary = new WorkflowExecutionSummary();
        summary.StepTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["tool_call"] = 1,
        };
        summary.StepTypeCounts.Should().Contain("tool_call", 1);
        summary.StepTypeCounts = null!;
        summary.StepTypeCounts.Should().BeEmpty();
    }
}
