using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Infrastructure.Reporting;
using FluentAssertions;
using System.Text.Json;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowRunReportExportWriterTests
{
    private const string AuditSentinel = "audit-secret-sentinel";

    [Fact]
    public void BuildDefaultPaths_ShouldCreateDirectory_AndUseWorkflowExecutionPrefix()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "aevatar-report-" + Guid.NewGuid().ToString("N"));

        try
        {
            var (jsonPath, htmlPath) = WorkflowRunReportExportWriter.BuildDefaultPaths(outputDir);

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
            await WorkflowRunReportExportWriter.WriteAsync(report, jsonPath, htmlPath);

            File.Exists(jsonPath).Should().BeTrue();
            File.Exists(htmlPath).Should().BeTrue();

            var json = await File.ReadAllTextAsync(jsonPath);
            using (var doc = JsonDocument.Parse(json))
            {
                doc.RootElement.GetProperty("workflowName").GetString().Should().Be("wf<main>");
                doc.RootElement.GetProperty("commandId").GetString().Should().Be("cmd-1");
                doc.RootElement.GetProperty("finalError").GetString().Should().Be("bad <error>");
            }

            var html = await File.ReadAllTextAsync(htmlPath);
            html.Should().Contain("Workflow Execution Report");
            html.Should().Contain("&lt;prompt&gt;&amp;input");
            html.Should().Contain("Error: bad &lt;error&gt;");
            html.Should().Contain("parent-1");
            html.Should().Contain("role-a");
            html.Should().Contain("workflow.completed");
            html.Should().Contain("Usage.PromptTokens");
            html.Should().Contain("gpt-5.4");
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
            await WorkflowRunReportExportWriter.WriteAsync(report, jsonPath, htmlPath);

            var html = await File.ReadAllTextAsync(htmlPath);
            html.Should().Contain("(no links)");
            html.Should().Contain("(no role replies captured)");
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task WriteAsync_ShouldSanitizePayloadDerivedFields_InJsonAndHtmlFiles()
    {
        var report = BuildReport(
            finalError: $"Bearer {AuditSentinel}",
            withTopology: true,
            withRoleReplies: true);
        report.Input = $$"""{"prompt":"go","token":"{{AuditSentinel}}"}""";
        report.FinalOutput = $$"""{"answer":"done","api_key":"{{AuditSentinel}}"}""";
        report.Steps[0].RequestParameters["authorization"] = $"Bearer {AuditSentinel}";
        report.Steps[0].CompletionAnnotations["access_token"] = AuditSentinel;
        report.Steps[0].AssignedVariable = "password";
        report.Steps[0].AssignedValue = AuditSentinel;
        report.RoleReplies[0].Content = $"reply Bearer {AuditSentinel}";
        report.Timeline[0].Message = $"signature=sha256={new string('a', 16)}{AuditSentinel}";
        report.Timeline[0].Data["result_json"] = $$"""{"secret":"{{AuditSentinel}}"}""";

        var outputDir = Path.Combine(Path.GetTempPath(), "aevatar-report-" + Guid.NewGuid().ToString("N"));
        var jsonPath = Path.Combine(outputDir, "report.json");
        var htmlPath = Path.Combine(outputDir, "report.html");

        try
        {
            await WorkflowRunReportExportWriter.WriteAsync(report, jsonPath, htmlPath);

            var json = await File.ReadAllTextAsync(jsonPath);
            var html = await File.ReadAllTextAsync(htmlPath);
            json.Should().NotContain(AuditSentinel);
            html.Should().NotContain(AuditSentinel);

            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("input").GetString().Should().Contain("[redacted]");
            doc.RootElement.GetProperty("roleReplies")[0].GetProperty("contentLength").GetInt32()
                .Should().Be(doc.RootElement.GetProperty("roleReplies")[0].GetProperty("content").GetString()!.Length);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }


    private static WorkflowRunReport BuildReport(
        string finalError,
        bool withTopology,
        bool withRoleReplies)
    {
        var started = DateTimeOffset.UtcNow;
        return new WorkflowRunReport
        {
            WorkflowName = "wf<main>",
            RootActorId = "root&1",
            CommandId = "cmd-1",
            StartedAt = started,
            EndedAt = started.AddSeconds(2),
            DurationMs = 2000,
            Success = true,
            Input = "<prompt>&input",
            FinalOutput = "ok <done>",
            FinalError = finalError,
            Topology = withTopology
                ? [new WorkflowRunTopologyEdge("parent-1", "child-1")]
                : [],
            Steps =
            [
                new WorkflowRunStepTrace
                {
                    StepId = "step-1",
                    StepType = "llm_call",
                    TargetRole = "researcher",
                    RequestedAt = started,
                    CompletedAt = started.AddMilliseconds(500),
                    Success = true,
                    WorkerId = "worker-1",
                    OutputPreview = "preview",
                    Error = "",
                    RequestParameters = new Dictionary<string, string> { ["k"] = "v" },
                    CompletionAnnotations = new Dictionary<string, string> { ["status"] = "ok" },
                },
            ],
            RoleReplies = withRoleReplies
                ?
                [
                    new WorkflowRunRoleReply
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
                new WorkflowRunTimelineEvent
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
            Usage = new WorkflowRunUsageMetrics
            {
                PromptTokens = 21,
                CompletionTokens = 34,
                TotalTokens = 55,
                Model = "gpt-5.4",
                Cost = 0.78,
                LatencyMs = 456,
            },
            Summary = new WorkflowRunStatistics
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
