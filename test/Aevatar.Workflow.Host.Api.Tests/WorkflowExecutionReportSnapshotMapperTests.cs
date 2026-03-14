using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
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
            LastEventId = string.Empty,
            RootActorId = string.Empty,
            CommandId = string.Empty,
            ReportVersion = string.Empty,
            WorkflowName = string.Empty,
            Input = string.Empty,
            FinalOutput = string.Empty,
            FinalError = string.Empty,
            Topology = [],
            Steps =
            [
                new WorkflowExecutionStepTrace
                {
                    StepId = string.Empty,
                    StepType = string.Empty,
                    TargetRole = string.Empty,
                    WorkerId = string.Empty,
                    OutputPreview = string.Empty,
                    Error = string.Empty,
                    RequestParameters = new Dictionary<string, string>(StringComparer.Ordinal),
                    CompletionAnnotations = new Dictionary<string, string>(StringComparer.Ordinal),
                    NextStepId = string.Empty,
                    BranchKey = string.Empty,
                    AssignedVariable = string.Empty,
                    AssignedValue = string.Empty,
                    SuspensionType = string.Empty,
                    SuspensionPrompt = string.Empty,
                    SuspensionTimeoutSeconds = 0,
                    RequestedVariableName = string.Empty,
                },
            ],
            RoleReplies =
            [
                new WorkflowExecutionRoleReply
                {
                    RoleId = string.Empty,
                    SessionId = string.Empty,
                    Content = string.Empty,
                },
            ],
            Timeline =
            [
                new WorkflowExecutionTimelineEvent
                {
                    Stage = string.Empty,
                    Message = string.Empty,
                    AgentId = string.Empty,
                    StepId = string.Empty,
                    StepType = string.Empty,
                    EventType = string.Empty,
                    Data = new Dictionary<string, string>(StringComparer.Ordinal),
                },
            ],
            Summary = new WorkflowExecutionSummary(),
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
            TypeUrl = $"type.googleapis.com/{WorkflowExecutionReport.Descriptor.FullName}",
            Value = ByteString.CopyFromUtf8("not-a-valid-protobuf"),
        };
        WorkflowExecutionReportSnapshotMapper.TryUnpack(corrupted, out report).Should().BeFalse();
        report.Should().BeNull();
    }
}
