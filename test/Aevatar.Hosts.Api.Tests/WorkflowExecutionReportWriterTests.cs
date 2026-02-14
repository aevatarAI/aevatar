using Aevatar.CQRS.Projections.Abstractions.ReadModels;
using Aevatar.Hosts.Api.Reporting;
using FluentAssertions;
using System.Text.Json;

namespace Aevatar.Hosts.Api.Tests;

public class WorkflowExecutionReportWriterTests
{
    [Fact]
    public void BuildDefaultPaths_ShouldCreateDirectory_AndUseWorkflowExecutionPrefix()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "aevatar-report-" + Guid.NewGuid().ToString("N"));

        try
        {
            var (jsonPath, htmlPath) = WorkflowExecutionReportWriter.BuildDefaultPaths(outputDir);

            Directory.Exists(outputDir).Should().BeTrue();
            Path.GetFileName(jsonPath).Should().StartWith("workflow-execution-");
            Path.GetFileName(jsonPath).Should().EndWith(".json");
            Path.GetFileName(htmlPath).Should().StartWith("workflow-execution-");
            Path.GetFileName(htmlPath).Should().EndWith(".html");
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task WriteAsync_ShouldWriteJsonAndHtml_WithEscapedContentAndSections()
    {
        var report = BuildReport(
            finalError: "bad <error>",
            withTopology: true,
            withRoleReplies: true);
        var outputDir = Path.Combine(Path.GetTempPath(), "aevatar-report-" + Guid.NewGuid().ToString("N"));
        var jsonPath = Path.Combine(outputDir, "report.json");
        var htmlPath = Path.Combine(outputDir, "report.html");

        try
        {
            await WorkflowExecutionReportWriter.WriteAsync(report, jsonPath, htmlPath);

            File.Exists(jsonPath).Should().BeTrue();
            File.Exists(htmlPath).Should().BeTrue();

            var json = await File.ReadAllTextAsync(jsonPath);
            using (var doc = JsonDocument.Parse(json))
            {
                doc.RootElement.GetProperty("workflowName").GetString().Should().Be("wf<main>");
                doc.RootElement.GetProperty("runId").GetString().Should().Be("run-1");
                doc.RootElement.GetProperty("finalError").GetString().Should().Be("bad <error>");
            }

            var html = await File.ReadAllTextAsync(htmlPath);
            html.Should().Contain("Workflow Execution Report");
            html.Should().Contain("&lt;prompt&gt;&amp;input");
            html.Should().Contain("Error: bad &lt;error&gt;");
            html.Should().Contain("parent-1");
            html.Should().Contain("role-a");
            html.Should().Contain("workflow.completed");
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task WriteAsync_WhenTopologyAndRepliesEmpty_ShouldRenderEmptyState()
    {
        var report = BuildReport(
            finalError: "",
            withTopology: false,
            withRoleReplies: false);
        var outputDir = Path.Combine(Path.GetTempPath(), "aevatar-report-" + Guid.NewGuid().ToString("N"));
        var jsonPath = Path.Combine(outputDir, "report.json");
        var htmlPath = Path.Combine(outputDir, "report.html");

        try
        {
            await WorkflowExecutionReportWriter.WriteAsync(report, jsonPath, htmlPath);

            var html = await File.ReadAllTextAsync(htmlPath);
            html.Should().Contain("(no links)");
            html.Should().Contain("(no role replies captured)");
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    private static WorkflowExecutionReport BuildReport(
        string finalError,
        bool withTopology,
        bool withRoleReplies)
    {
        var started = DateTimeOffset.UtcNow;
        return new WorkflowExecutionReport
        {
            WorkflowName = "wf<main>",
            RootActorId = "root&1",
            RunId = "run-1",
            StartedAt = started,
            EndedAt = started.AddSeconds(2),
            DurationMs = 2000,
            Success = true,
            Input = "<prompt>&input",
            FinalOutput = "ok <done>",
            FinalError = finalError,
            Topology = withTopology
                ? [new WorkflowExecutionTopologyEdge("parent-1", "child-1")]
                : [],
            Steps =
            [
                new WorkflowExecutionStepTrace
                {
                    StepId = "step-1",
                    StepType = "llm_call",
                    RunId = "run-1",
                    TargetRole = "researcher",
                    RequestedAt = started,
                    CompletedAt = started.AddMilliseconds(500),
                    Success = true,
                    WorkerId = "worker-1",
                    OutputPreview = "preview",
                    Error = "",
                    RequestParameters = new Dictionary<string, string> { ["k"] = "v" },
                    CompletionMetadata = new Dictionary<string, string> { ["status"] = "ok" },
                },
            ],
            RoleReplies = withRoleReplies
                ?
                [
                    new WorkflowExecutionRoleReply
                    {
                        Timestamp = started.AddMilliseconds(700),
                        RoleId = "role-a",
                        SessionId = "s-1",
                        Content = "reply",
                        ContentLength = 5,
                    },
                ]
                : [],
            Timeline =
            [
                new WorkflowExecutionTimelineEvent
                {
                    Timestamp = started,
                    Stage = "workflow.completed",
                    Message = "done",
                    AgentId = "root&1",
                    StepId = "step-1",
                    StepType = "llm_call",
                    EventType = "WorkflowCompletedEvent",
                    Data = new Dictionary<string, string> { ["ok"] = "true" },
                },
            ],
            Summary = new WorkflowExecutionSummary
            {
                TotalSteps = 1,
                RequestedSteps = 1,
                CompletedSteps = 1,
                RoleReplyCount = withRoleReplies ? 1 : 0,
                StepTypeCounts = new Dictionary<string, int> { ["llm_call"] = 1 },
            },
        };
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // no-op
        }
    }
}
