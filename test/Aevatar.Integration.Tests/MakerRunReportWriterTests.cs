using Aevatar.Maker.Projection;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

public class MakerRunReportWriterTests
{
    [Fact]
    public async Task WriteAsync_ShouldRenderRichHtml_WhenReportContainsData()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "aevatar-maker-report-tests", Guid.NewGuid().ToString("N"));
        var (jsonPath, htmlPath) = MakerRunReportWriter.BuildDefaultPaths(outputDirectory);

        var report = new MakerRunReport
        {
            WorkflowName = "maker_report",
            WorkflowPath = "workflow.yaml",
            RootActorId = "root-1",
            Provider = "test-provider",
            Model = "test-model",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-3),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = 3000,
            TimedOut = false,
            Success = true,
            Input = "input text",
            FinalOutput = "final output",
            FinalError = "simulated error",
            Topology =
            [
                new MakerTopologyEdge("parent-1", "child-1"),
            ],
            Steps =
            [
                new MakerStepTrace
                {
                    StepId = "step-1",
                    StepType = "maker_vote",
                    TargetRole = "coordinator",
                    RequestedAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                    CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                    Success = true,
                    WorkerId = "worker-1",
                    OutputPreview = "preview",
                    CompletionMetadata = new Dictionary<string, string>
                    {
                        ["maker_vote.red_flagged"] = "1",
                    },
                },
            ],
            RoleReplies =
            [
                new MakerRoleReply
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    RoleId = "worker-1",
                    SessionId = "session-1",
                    Content = "reply-content",
                    ContentLength = "reply-content".Length,
                },
            ],
            Timeline =
            [
                new MakerTimelineEvent
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Stage = "maker.vote",
                    Message = "vote completed",
                    AgentId = "worker-1",
                    StepId = "step-1",
                    StepType = "maker_vote",
                    EventType = "event-type",
                },
            ],
            Summary = new MakerRunSummary
            {
                TotalSteps = 1,
                RequestedSteps = 1,
                CompletedSteps = 1,
                VoteSteps = 1,
                ConnectorSteps = 0,
                TotalRedFlaggedCandidates = 1,
                RoleReplyCount = 1,
                StepTypeCounts = new Dictionary<string, int>
                {
                    ["maker_vote"] = 1,
                },
                ConnectorTypeCounts = new Dictionary<string, int>
                {
                    ["http"] = 1,
                },
            },
            Verification = new MakerRunVerification
            {
                FullFlowPassed = false,
                Checks =
                [
                    new MakerVerificationCheck { Name = "check-pass", Passed = true, Evidence = "ok" },
                    new MakerVerificationCheck { Name = "check-fail", Passed = false, Evidence = "bad" },
                ],
                FailedChecks = ["check-fail"],
                Warnings = ["warning-1"],
            },
        };

        await MakerRunReportWriter.WriteAsync(report, jsonPath, htmlPath);

        File.Exists(jsonPath).Should().BeTrue();
        File.Exists(htmlPath).Should().BeTrue();

        var html = await File.ReadAllTextAsync(htmlPath);
        html.Should().Contain("MAKER Execution Report");
        html.Should().Contain("FailedChecks");
        html.Should().Contain("Warnings");
        html.Should().Contain("Role Replies");
        html.Should().Contain("Topology");
        html.Should().Contain("Error:");

        Directory.Delete(outputDirectory, recursive: true);
    }

    [Fact]
    public async Task WriteAsync_ShouldRenderEmptySections_WhenCollectionsAreEmpty()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "aevatar-maker-report-tests", Guid.NewGuid().ToString("N"));
        var jsonPath = Path.Combine(outputDirectory, "nested", "report.json");
        var htmlPath = Path.Combine(outputDirectory, "nested", "report.html");

        var report = new MakerRunReport
        {
            WorkflowName = "empty-report",
            Summary = new MakerRunSummary(),
            Verification = new MakerRunVerification(),
        };

        await MakerRunReportWriter.WriteAsync(report, jsonPath, htmlPath);

        var html = await File.ReadAllTextAsync(htmlPath);
        html.Should().Contain("(no verification checks)");
        html.Should().Contain("(no links)");
        html.Should().Contain("(no role replies captured)");

        Directory.Delete(outputDirectory, recursive: true);
    }

    [Fact]
    public void MakerStepTrace_DurationMs_ShouldReturnValueOnlyWhenBothTimestampsExist()
    {
        var completed = new MakerStepTrace
        {
            RequestedAt = DateTimeOffset.UtcNow.AddSeconds(-2),
            CompletedAt = DateTimeOffset.UtcNow,
        };
        var pending = new MakerStepTrace
        {
            RequestedAt = DateTimeOffset.UtcNow.AddSeconds(-2),
            CompletedAt = null,
        };

        completed.DurationMs.Should().NotBeNull();
        completed.DurationMs.Should().BeGreaterThan(0);
        pending.DurationMs.Should().BeNull();
    }
}
