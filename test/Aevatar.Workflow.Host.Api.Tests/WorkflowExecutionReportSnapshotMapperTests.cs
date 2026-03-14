using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Transport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionReportSnapshotMapperTests
{
    [Fact]
    public void PackAndTryUnpack_ShouldRoundTripRichWorkflowExecutionReport()
    {
        var report = new WorkflowExecutionReport
        {
            Id = "report-1",
            StateVersion = 42,
            LastEventId = "evt-1",
            CreatedAt = new DateTimeOffset(2026, 3, 11, 8, 0, 0, TimeSpan.FromHours(8)),
            UpdatedAt = new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.FromHours(8)),
            RootActorId = "actor-1",
            CommandId = "cmd-1",
            ReportVersion = "2.0",
            ProjectionScope = WorkflowExecutionProjectionScope.RunIsolated,
            TopologySource = WorkflowExecutionTopologySource.RuntimeSnapshot,
            CompletionStatus = WorkflowExecutionCompletionStatus.Completed,
            WorkflowName = "direct",
            StartedAt = new DateTimeOffset(2026, 3, 11, 8, 1, 0, TimeSpan.FromHours(8)),
            EndedAt = new DateTimeOffset(2026, 3, 11, 8, 2, 30, TimeSpan.FromHours(8)),
            DurationMs = 90000,
            Success = true,
            Input = "hello",
            FinalOutput = "done",
            FinalError = string.Empty,
            Topology =
            [
                new WorkflowExecutionTopologyEdge("actor-1", "child-1"),
            ],
            Steps =
            [
                new WorkflowExecutionStepTrace
                {
                    StepId = "step-1",
                    StepType = "llm_call",
                    TargetRole = "assistant",
                    RequestedAt = new DateTimeOffset(2026, 3, 11, 8, 1, 5, TimeSpan.FromHours(8)),
                    CompletedAt = new DateTimeOffset(2026, 3, 11, 8, 1, 15, TimeSpan.FromHours(8)),
                    Success = true,
                    WorkerId = "worker-1",
                    OutputPreview = "preview",
                    Error = string.Empty,
                    RequestParameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["temperature"] = "0.2",
                    },
                    CompletionAnnotations = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["branch"] = "approved",
                    },
                    NextStepId = "step-2",
                    BranchKey = "approved",
                    AssignedVariable = "answer",
                    AssignedValue = "42",
                    SuspensionType = "approval",
                    SuspensionPrompt = "Need approval",
                    SuspensionTimeoutSeconds = 30,
                    RequestedVariableName = "user_input",
                },
            ],
            RoleReplies =
            [
                new WorkflowExecutionRoleReply
                {
                    Timestamp = new DateTimeOffset(2026, 3, 11, 8, 1, 20, TimeSpan.FromHours(8)),
                    RoleId = "assistant",
                    SessionId = "session-1",
                    Content = "reply",
                    ContentLength = 5,
                },
            ],
            Timeline =
            [
                new WorkflowExecutionTimelineEvent
                {
                    Timestamp = new DateTimeOffset(2026, 3, 11, 8, 1, 25, TimeSpan.FromHours(8)),
                    Stage = "completed",
                    Message = "step done",
                    AgentId = "agent-1",
                    StepId = "step-1",
                    StepType = "llm_call",
                    EventType = "StepCompletedEvent",
                    Data = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["key"] = "value",
                    },
                },
            ],
            Summary = new WorkflowExecutionSummary
            {
                TotalSteps = 2,
                RequestedSteps = 2,
                CompletedSteps = 1,
                RoleReplyCount = 1,
                StepTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["llm_call"] = 1,
                },
            },
        };

        var payload = WorkflowExecutionReportSnapshotMapper.Pack(report);
        var ok = WorkflowExecutionReportSnapshotMapper.TryUnpack(payload, out var unpacked);

        ok.Should().BeTrue();
        unpacked.Should().NotBeNull();
        unpacked!.Id.Should().Be("report-1");
        unpacked.StateVersion.Should().Be(42);
        unpacked.LastEventId.Should().Be("evt-1");
        unpacked.RootActorId.Should().Be("actor-1");
        unpacked.CommandId.Should().Be("cmd-1");
        unpacked.ReportVersion.Should().Be("2.0");
        unpacked.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Completed);
        unpacked.WorkflowName.Should().Be("direct");
        unpacked.Success.Should().BeTrue();
        unpacked.Topology.Should().ContainSingle().Which.Should().Be(new WorkflowExecutionTopologyEdge("actor-1", "child-1"));
        unpacked.Steps.Should().ContainSingle();
        unpacked.Steps[0].StepId.Should().Be("step-1");
        unpacked.Steps[0].RequestParameters.Should().ContainKey("temperature").WhoseValue.Should().Be("0.2");
        unpacked.Steps[0].CompletionAnnotations.Should().ContainKey("branch").WhoseValue.Should().Be("approved");
        unpacked.Steps[0].SuspensionTimeoutSeconds.Should().Be(30);
        unpacked.RoleReplies.Should().ContainSingle();
        unpacked.RoleReplies[0].RoleId.Should().Be("assistant");
        unpacked.Timeline.Should().ContainSingle();
        unpacked.Timeline[0].Data.Should().ContainKey("key").WhoseValue.Should().Be("value");
        unpacked.Summary.StepTypeCounts.Should().ContainKey("llm_call").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void PackAndTryUnpack_ShouldNormalizeSparseValuesToDefaults()
    {
        var report = new WorkflowExecutionReport
        {
            Id = "report-2",
            LastEventId = null!,
            RootActorId = null!,
            CommandId = null!,
            ReportVersion = null!,
            WorkflowName = null!,
            Input = null!,
            FinalOutput = null!,
            FinalError = null!,
            Topology = null!,
            Steps =
            [
                new WorkflowExecutionStepTrace
                {
                    StepId = null!,
                    StepType = null!,
                    TargetRole = null!,
                    WorkerId = null!,
                    OutputPreview = null!,
                    Error = null!,
                    RequestParameters = null!,
                    CompletionAnnotations = null!,
                    NextStepId = null!,
                    BranchKey = null!,
                    AssignedVariable = null!,
                    AssignedValue = null!,
                    SuspensionType = null!,
                    SuspensionPrompt = null!,
                    SuspensionTimeoutSeconds = 0,
                    RequestedVariableName = null!,
                },
            ],
            RoleReplies =
            [
                new WorkflowExecutionRoleReply
                {
                    RoleId = null!,
                    SessionId = null!,
                    Content = null!,
                },
            ],
            Timeline =
            [
                new WorkflowExecutionTimelineEvent
                {
                    Stage = null!,
                    Message = null!,
                    AgentId = null!,
                    StepId = null!,
                    StepType = null!,
                    EventType = null!,
                    Data = null!,
                },
            ],
            Summary = null!,
        };

        var payload = WorkflowExecutionReportSnapshotMapper.Pack(report);
        var ok = WorkflowExecutionReportSnapshotMapper.TryUnpack(payload, out var unpacked);

        ok.Should().BeTrue();
        unpacked.Should().NotBeNull();
        unpacked!.LastEventId.Should().BeEmpty();
        unpacked.RootActorId.Should().BeEmpty();
        unpacked.CommandId.Should().BeEmpty();
        unpacked.ReportVersion.Should().BeEmpty();
        unpacked.WorkflowName.Should().BeEmpty();
        unpacked.Input.Should().BeEmpty();
        unpacked.FinalOutput.Should().BeEmpty();
        unpacked.FinalError.Should().BeEmpty();
        unpacked.Topology.Should().BeEmpty();
        unpacked.Steps.Should().ContainSingle();
        unpacked.Steps[0].StepId.Should().BeEmpty();
        unpacked.Steps[0].RequestParameters.Should().BeEmpty();
        unpacked.Steps[0].CompletionAnnotations.Should().BeEmpty();
        unpacked.Steps[0].SuspensionTimeoutSeconds.Should().BeNull();
        unpacked.RoleReplies[0].RoleId.Should().BeEmpty();
        unpacked.RoleReplies[0].SessionId.Should().BeEmpty();
        unpacked.RoleReplies[0].Content.Should().BeEmpty();
        unpacked.Timeline[0].Stage.Should().BeEmpty();
        unpacked.Timeline[0].Data.Should().BeEmpty();
        unpacked.Summary.TotalSteps.Should().Be(0);
        unpacked.Summary.StepTypeCounts.Should().BeEmpty();
    }

    [Fact]
    public void TryUnpack_ShouldRejectNullWrongTypeAndCorruptedPayload()
    {
        WorkflowExecutionReport? report;

        WorkflowExecutionReportSnapshotMapper.TryUnpack(null, out report).Should().BeFalse();
        report.Should().BeNull();

        WorkflowExecutionReportSnapshotMapper.TryUnpack(
            Any.Pack(new StringValue { Value = "wrong" }),
            out report).Should().BeFalse();
        report.Should().BeNull();

        var corrupted = new Any
        {
            TypeUrl = $"type.googleapis.com/{WorkflowExecutionReportSnapshot.Descriptor.FullName}",
            Value = ByteString.CopyFromUtf8("not-a-valid-protobuf"),
        };
        WorkflowExecutionReportSnapshotMapper.TryUnpack(corrupted, out report).Should().BeFalse();
        report.Should().BeNull();
    }
}
